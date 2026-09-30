//! Images CRUD + batch operations (mirrors image-related endpoints in main.py).

use rusqlite::params;
use std::path::Path;

use crate::db::{get_conn, DbPool};
use crate::error::{AppError, AppResult};
use crate::models::request::ImageListResponse;
use crate::models::{Image, ImageDetail, Stats};
use crate::utils::{collect_image_files, split_tags};
use crate::imageproc::io;

/// List images with filtering, sorting, and pagination.
pub fn list_images(
    pool: &DbPool,
    folder_like: Option<&str>,
    status: Option<&str>,
    tags: &[String],
    tag_mode: &str,
    sort: &str,
    page: i64,
    per_page: i64,
) -> AppResult<ImageListResponse> {
    let conn = get_conn(pool)?;
    let mut conditions: Vec<String> = Vec::new();
    let mut params_vec: Vec<Box<dyn rusqlite::ToSql>> = Vec::new();

    if let Some(like) = folder_like {
        conditions.push("path LIKE ?".to_string());
        params_vec.push(Box::new(format!("{}%", like)));
    }
    if let Some(st) = status {
        conditions.push("status = ?".to_string());
        params_vec.push(Box::new(st.to_string()));
    }

    if !tags.is_empty() {
        if tag_mode.eq_ignore_ascii_case("or") {
            let placeholders: Vec<String> = tags.iter().map(|_| "?".to_string()).collect();
            conditions.push(format!(
                "id IN (SELECT image_id FROM image_tags JOIN tags ON tags.id=image_tags.tag_id WHERE tags.name IN ({}))",
                placeholders.join(",")
            ));
            for t in tags {
                params_vec.push(Box::new(t.clone()));
            }
        } else {
            for t in tags {
                conditions.push(
                    "id IN (SELECT image_id FROM image_tags JOIN tags ON tags.id=image_tags.tag_id WHERE tags.name=?)".to_string(),
                );
                params_vec.push(Box::new(t.clone()));
            }
        }
    }

    let where_clause = if conditions.is_empty() {
        String::new()
    } else {
        format!("WHERE {}", conditions.join(" AND "))
    };

    // Count
    let count_sql = format!("SELECT COUNT(*) FROM images {}", where_clause);
    let total: i64 = conn.query_row(
        &count_sql,
        rusqlite::params_from_iter(params_vec.iter().map(|b| b.as_ref())),
        |r| r.get(0),
    )?;

    // Order by
    let order_by = match sort {
        "date_desc" => "date_taken DESC, id DESC",
        "date_asc" => "date_taken ASC, id ASC",
        "newest" => "id DESC",
        "oldest" => "id ASC",
        "name" => "filename ASC",
        "size" => "file_size DESC",
        _ => "date_taken DESC, id DESC",
    };

    let offset = (page - 1) * per_page;
    let sql = format!(
        "SELECT * FROM images {} ORDER BY {} LIMIT ? OFFSET ?",
        where_clause, order_by
    );

    let mut stmt = conn.prepare(&sql)?;
    let param_refs: Vec<&dyn rusqlite::ToSql> = params_vec
        .iter()
        .map(|b| b.as_ref())
        .chain([(&per_page as &dyn rusqlite::ToSql), (&offset)])
        .collect();
    let rows = stmt.query_map(param_refs.as_slice(), map_image)?;
    let images: Vec<serde_json::Value> = rows.filter_map(|r| r.ok())
        .map(|img| img.to_dict_with_tags())
        .collect();

    Ok(ImageListResponse {
        total,
        page,
        per_page,
        images,
    })
}

/// Get a single image with prev/next IDs and tag list.
pub fn get_image(pool: &DbPool, image_id: i64, folder_like: Option<&str>) -> AppResult<ImageDetail> {
    let conn = get_conn(pool)?;
    let img: Image = conn
        .query_row("SELECT * FROM images WHERE id=?", params![image_id], map_image)
        .map_err(|_| AppError::not_found("Image not found"))?;

    // Prev/next
    let (prev_id, next_id) = if let Some(like) = folder_like {
        let prev: Option<i64> = conn
            .query_row(
                "SELECT id FROM images WHERE id < ? AND path LIKE ? ORDER BY id DESC LIMIT 1",
                params![image_id, format!("{}%", like)],
                |r| r.get(0),
            )
            .ok();
        let next: Option<i64> = conn
            .query_row(
                "SELECT id FROM images WHERE id > ? AND path LIKE ? ORDER BY id ASC LIMIT 1",
                params![image_id, format!("{}%", like)],
                |r| r.get(0),
            )
            .ok();
        (prev, next)
    } else {
        let prev: Option<i64> = conn
            .query_row(
                "SELECT id FROM images WHERE id < ? ORDER BY id DESC LIMIT 1",
                params![image_id],
                |r| r.get(0),
            )
            .ok();
        let next: Option<i64> = conn
            .query_row(
                "SELECT id FROM images WHERE id > ? ORDER BY id ASC LIMIT 1",
                params![image_id],
                |r| r.get(0),
            )
            .ok();
        (prev, next)
    };

    // Structured tags
    let mut tag_stmt = conn.prepare(
        "SELECT tags.name FROM image_tags JOIN tags ON tags.id=image_tags.tag_id WHERE image_tags.image_id=? ORDER BY tags.name",
    )?;
    let tag_list: Vec<String> = tag_stmt
        .query_map(params![image_id], |r| r.get::<_, String>(0))?
        .filter_map(|r| r.ok())
        .collect();

    Ok(ImageDetail {
        image: img,
        prev_id,
        next_id,
        tag_list,
    })
}

/// Update image description/tags.
pub fn update_image(pool: &DbPool, image_id: i64, description: Option<&str>, tags: Option<&str>) -> AppResult<()> {
    let conn = get_conn(pool)?;
    let row = conn
        .query_row("SELECT description, tags FROM images WHERE id=?", params![image_id], |r| {
            Ok((r.get::<_, String>(0)?, r.get::<_, String>(1)?))
        })
        .map_err(|_| AppError::not_found("Image not found"))?;
    let desc = description.unwrap_or(&row.0);
    let tags = tags.unwrap_or(&row.1);
    conn.execute(
        "UPDATE images SET description=?, tags=?, status='done' WHERE id=?",
        params![desc, tags, image_id],
    )?;
    super::tags::sync_image_tags(&conn, image_id, tags)?;
    Ok(())
}

/// Scan a folder and add new images to DB.
pub fn scan_folder(pool: &DbPool, folder: &Path) -> AppResult<(i64, i64)> {
    let folder_str = folder.to_string_lossy().to_string();
    let images = collect_image_files(folder);
    let conn = get_conn(pool)?;
    let mut added = 0i64;
    for img_path in &images {
        let abs_path = img_path.to_string_lossy().to_string();
        let exists: bool = conn
            .query_row("SELECT 1 FROM images WHERE path=?", params![abs_path], |_| Ok(true))
            .unwrap_or(false);
        if !exists {
            let (w, h, size) = io::read_image_meta(img_path).unwrap_or((0, 0, 0));
            conn.execute(
                "INSERT INTO images (path, filename, status, width, height, file_size) VALUES (?, ?, 'pending', ?, ?, ?)",
                params![abs_path, img_path.file_name().unwrap_or_default().to_string_lossy(), w as i64, h as i64, size as i64],
            )?;
            added += 1;
        }
    }
    // Save folder
    conn.execute("INSERT OR IGNORE INTO folders (path) VALUES (?)", params![folder_str])?;
    // Clean deleted files
    let like = format!("{}%", folder_str);
    let mut stmt = conn.prepare("SELECT id, path FROM images WHERE path LIKE ?")?;
    let to_delete: Vec<(i64, String)> = stmt
        .query_map(params![like], |r| Ok((r.get(0)?, r.get(1)?)))?
        .filter_map(|r| r.ok())
        .filter(|(_, p)| !Path::new(p).exists())
        .collect();
    drop(stmt);
    for (id, _) in &to_delete {
        conn.execute("DELETE FROM images WHERE id=?", params![id])?;
    }
    let total: i64 = conn.query_row(
        "SELECT COUNT(*) FROM images WHERE path LIKE ?",
        params![like],
        |r| r.get(0),
    )?;
    Ok((added, total))
}

/// Rescan a folder: remove deleted, add new.
pub fn rescan_folder(pool: &DbPool, folder: &Path) -> AppResult<(i64, i64, i64)> {
    let folder_str = folder.to_string_lossy().to_string();
    let conn = get_conn(pool)?;
    let like = format!("{}%", folder_str);

    // Remove deleted
    let mut stmt = conn.prepare("SELECT id, path FROM images WHERE path LIKE ?")?;
    let to_delete: Vec<(i64, String)> = stmt
        .query_map(params![like], |r| Ok((r.get(0)?, r.get(1)?)))?
        .filter_map(|r| r.ok())
        .filter(|(_, p)| !Path::new(p).exists())
        .collect();
    drop(stmt);
    let removed = to_delete.len() as i64;
    for (id, _) in &to_delete {
        conn.execute("DELETE FROM images WHERE id=?", params![id])?;
    }

    // Add new
    let images = collect_image_files(folder);
    let mut added = 0i64;
    for img_path in &images {
        let abs_path = img_path.to_string_lossy().to_string();
        let exists: bool = conn
            .query_row("SELECT 1 FROM images WHERE path=?", params![abs_path], |_| Ok(true))
            .unwrap_or(false);
        if !exists {
            let (w, h, size) = io::read_image_meta(img_path).unwrap_or((0, 0, 0));
            conn.execute(
                "INSERT INTO images (path, filename, status, width, height, file_size) VALUES (?, ?, 'pending', ?, ?, ?)",
                params![abs_path, img_path.file_name().unwrap_or_default().to_string_lossy(), w as i64, h as i64, size as i64],
            )?;
            added += 1;
        }
    }
    let total: i64 = conn.query_row(
        "SELECT COUNT(*) FROM images WHERE path LIKE ?",
        params![like],
        |r| r.get(0),
    )?;
    Ok((removed, added, total))
}

/// Delete a single image record.
pub fn delete_image(pool: &DbPool, image_id: i64) -> AppResult<String> {
    let conn = get_conn(pool)?;
    let path: String = conn
        .query_row("SELECT path FROM images WHERE id=?", params![image_id], |r| r.get(0))
        .map_err(|_| AppError::not_found("Image not found"))?;
    conn.execute("DELETE FROM images WHERE id=?", params![image_id])?;
    Ok(path)
}

/// Batch delete image records.
pub fn batch_delete(pool: &DbPool, ids: &[i64]) -> AppResult<(i64, Vec<serde_json::Value>, std::collections::HashMap<i64, String>)> {
    let conn = get_conn(pool)?;
    let placeholders: Vec<String> = ids.iter().map(|_| "?".to_string()).collect();
    let sql = format!("SELECT id, path FROM images WHERE id IN ({})", placeholders.join(","));
    let mut stmt = conn.prepare(&sql)?;
    let params_refs: Vec<&dyn rusqlite::ToSql> = ids.iter().map(|id| id as &dyn rusqlite::ToSql).collect();
    let rows: Vec<(i64, String)> = stmt
        .query_map(params_refs.as_slice(), |r| Ok((r.get(0)?, r.get(1)?)))?
        .filter_map(|r| r.ok())
        .collect();
    drop(stmt);

    let mut id_to_path: std::collections::HashMap<i64, String> = std::collections::HashMap::new();
    for (id, path) in &rows {
        id_to_path.insert(*id, path.clone());
    }

    let mut deleted = 0i64;
    let mut failures: Vec<serde_json::Value> = Vec::new();
    for id in ids {
        if !id_to_path.contains_key(id) {
            failures.push(serde_json::json!({"id": id, "error": "Image not found"}));
            continue;
        }
        match conn.execute("DELETE FROM images WHERE id=?", params![id]) {
            Ok(_) => deleted += 1,
            Err(e) => failures.push(serde_json::json!({"id": id, "error": e.to_string()})),
        }
    }
    Ok((deleted, failures, id_to_path))
}

/// Batch add/remove a tag on multiple images.
pub fn batch_tag(pool: &DbPool, ids: &[i64], tag: &str, mode: &str) -> AppResult<(i64, i64)> {
    let conn = get_conn(pool)?;
    let placeholders: Vec<String> = ids.iter().map(|_| "?".to_string()).collect();
    let sql = format!("SELECT id, tags FROM images WHERE id IN ({})", placeholders.join(","));
    let mut stmt = conn.prepare(&sql)?;
    let params_refs: Vec<&dyn rusqlite::ToSql> = ids.iter().map(|id| id as &dyn rusqlite::ToSql).collect();
    let rows: Vec<(i64, String)> = stmt
        .query_map(params_refs.as_slice(), |r| Ok((r.get(0)?, r.get(1)?)))?
        .filter_map(|r| r.ok())
        .collect();
    drop(stmt);

    let mut updated = 0i64;
    for (id, tags_str) in rows {
        let mut current = split_tags(&tags_str);
        let changed = match mode {
            "add" => {
                if current.iter().any(|t| t == tag) {
                    false
                } else {
                    current.push(tag.to_string());
                    true
                }
            }
            "remove" => {
                let before = current.len();
                current.retain(|t| t != tag);
                current.len() != before
            }
            _ => false,
        };
        if changed {
            let new_str = current.join(", ");
            conn.execute("UPDATE images SET tags=?, status='done' WHERE id=?", params![new_str, id])?;
            super::tags::sync_image_tags(&conn, id, &new_str)?;
            updated += 1;
        }
    }
    Ok((updated, ids.len() as i64 - updated))
}

/// Get statistics.
pub fn stats(pool: &DbPool, folder_like: Option<&str>) -> AppResult<Stats> {
    let conn = get_conn(pool)?;
    let (total, done, pending, failed, tag_count, total_size) = if let Some(like) = folder_like {
        let pat = format!("{}%", like);
        let total: i64 = conn.query_row("SELECT COUNT(*) FROM images WHERE path LIKE ?", params![pat], |r| r.get(0))?;
        let done: i64 = conn.query_row("SELECT COUNT(*) FROM images WHERE path LIKE ? AND status='done'", params![pat], |r| r.get(0))?;
        let pending: i64 = conn.query_row("SELECT COUNT(*) FROM images WHERE path LIKE ? AND status='pending'", params![pat], |r| r.get(0))?;
        let failed: i64 = conn.query_row("SELECT COUNT(*) FROM images WHERE path LIKE ? AND status='failed'", params![pat], |r| r.get(0))?;
        let tag_count: i64 = conn.query_row(
            "SELECT COUNT(DISTINCT tags.id) FROM tags JOIN image_tags ON image_tags.tag_id=tags.id JOIN images ON images.id=image_tags.image_id WHERE images.path LIKE ?",
            params![pat], |r| r.get(0),
        )?;
        let total_size: i64 = conn.query_row("SELECT COALESCE(SUM(file_size),0) FROM images WHERE path LIKE ?", params![pat], |r| r.get(0))?;
        (total, done, pending, failed, tag_count, total_size)
    } else {
        let total: i64 = conn.query_row("SELECT COUNT(*) FROM images", [], |r| r.get(0))?;
        let done: i64 = conn.query_row("SELECT COUNT(*) FROM images WHERE status='done'", [], |r| r.get(0))?;
        let pending: i64 = conn.query_row("SELECT COUNT(*) FROM images WHERE status='pending'", [], |r| r.get(0))?;
        let failed: i64 = conn.query_row("SELECT COUNT(*) FROM images WHERE status='failed'", [], |r| r.get(0))?;
        let tag_count: i64 = conn.query_row(
            "SELECT COUNT(DISTINCT id) FROM tags WHERE id IN (SELECT tag_id FROM image_tags)",
            [], |r| r.get(0),
        )?;
        let total_size: i64 = conn.query_row("SELECT COALESCE(SUM(file_size),0) FROM images", [], |r| r.get(0))?;
        (total, done, pending, failed, tag_count, total_size)
    };
    Ok(Stats { total, done, pending, failed, tag_count, total_size })
}

/// Search images using FTS5.
pub fn search_images(
    pool: &DbPool,
    query: &str,
    folder_like: Option<&str>,
    tags: &[String],
    tag_mode: &str,
    page: i64,
    per_page: i64,
) -> AppResult<ImageListResponse> {
    let conn = get_conn(pool)?;
    let mut conditions: Vec<String> = Vec::new();
    let mut params_vec: Vec<Box<dyn rusqlite::ToSql>> = Vec::new();

    if !query.is_empty() {
        conditions.push("images_fts MATCH ?".to_string());
        params_vec.push(Box::new(query.to_string()));
    }
    if let Some(like) = folder_like {
        conditions.push("images.path LIKE ?".to_string());
        params_vec.push(Box::new(format!("{}%", like)));
    }

    if !tags.is_empty() {
        if tag_mode.eq_ignore_ascii_case("or") {
            let placeholders: Vec<String> = tags.iter().map(|_| "?".to_string()).collect();
            conditions.push(format!(
                "images.id IN (SELECT image_id FROM image_tags JOIN tags ON tags.id=image_tags.tag_id WHERE tags.name IN ({}))",
                placeholders.join(",")
            ));
            for t in tags {
                params_vec.push(Box::new(t.clone()));
            }
        } else {
            for t in tags {
                conditions.push("images.id IN (SELECT image_id FROM image_tags JOIN tags ON tags.id=image_tags.tag_id WHERE tags.name=?)".to_string());
                params_vec.push(Box::new(t.clone()));
            }
        }
    }

    if conditions.is_empty() {
        return Ok(ImageListResponse { total: 0, page, per_page, images: vec![] });
    }

    let where_clause = conditions.join(" AND ");
    let count_sql = format!(
        "SELECT COUNT(*) FROM images JOIN images_fts ON images.id = images_fts.rowid WHERE {}",
        where_clause
    );
    let total: i64 = conn.query_row(
        &count_sql,
        rusqlite::params_from_iter(params_vec.iter().map(|b| b.as_ref())),
        |r| r.get(0),
    )?;

    let offset = (page - 1) * per_page;
    let sql = format!(
        "SELECT images.* FROM images JOIN images_fts ON images.id = images_fts.rowid WHERE {} ORDER BY rank LIMIT ? OFFSET ?",
        where_clause
    );
    let mut stmt = conn.prepare(&sql)?;
    let param_refs: Vec<&dyn rusqlite::ToSql> = params_vec
        .iter()
        .map(|b| b.as_ref())
        .chain([(&per_page as &dyn rusqlite::ToSql), (&offset)])
        .collect();
    let rows = stmt.query_map(param_refs.as_slice(), map_image)?;
    let images: Vec<serde_json::Value> = rows.filter_map(|r| r.ok())
        .map(|img| img.to_dict_with_tags())
        .collect();

    Ok(ImageListResponse { total, page, per_page, images })
}

/// Map a rusqlite row to an Image.
fn map_image(r: &rusqlite::Row) -> rusqlite::Result<Image> {
    Ok(Image {
        id: r.get("id")?,
        path: r.get("path")?,
        filename: r.get("filename")?,
        description: r.get("description").unwrap_or_default(),
        tags: r.get("tags").unwrap_or_default(),
        status: r.get("status").unwrap_or_else(|_| "pending".to_string()),
        width: r.get("width").unwrap_or(0),
        height: r.get("height").unwrap_or(0),
        file_size: r.get("file_size").unwrap_or(0),
        date_taken: r.get("date_taken").unwrap_or_default(),
        faces_scanned: r.get("faces_scanned").unwrap_or(0),
        created_at: r.get("created_at").unwrap_or_default(),
    })
}

/// Get images by status for processing.
/// `force = true` 时不筛状态（用于「全部重新打标」），否则只取 `status='pending'`。
pub fn get_images_for_processing(
    pool: &DbPool,
    image_ids: &[i64],
    folder_like: Option<&str>,
    limit: i64,
    force: bool,
) -> AppResult<Vec<Image>> {
    let conn = get_conn(pool)?;
    let mut rows: Vec<Image> = if !image_ids.is_empty() {
        let placeholders: Vec<String> = image_ids.iter().map(|_| "?".to_string()).collect();
        let sql = format!("SELECT * FROM images WHERE id IN ({})", placeholders.join(","));
        let mut stmt = conn.prepare(&sql)?;
        let params_refs: Vec<&dyn rusqlite::ToSql> = image_ids.iter().map(|id| id as &dyn rusqlite::ToSql).collect();
        let iter = stmt.query_map(params_refs.as_slice(), map_image)?;
        iter.filter_map(|r| r.ok()).collect()
    } else if let Some(like) = folder_like {
        let pat = format!("{}%", like);
        let sql = if force {
            "SELECT * FROM images WHERE path LIKE ?"
        } else {
            "SELECT * FROM images WHERE path LIKE ? AND status='pending'"
        };
        let mut stmt = conn.prepare(sql)?;
        let iter = stmt.query_map(params![pat], map_image)?;
        iter.filter_map(|r| r.ok()).collect()
    } else {
        let sql = if force {
            "SELECT * FROM images"
        } else {
            "SELECT * FROM images WHERE status='pending'"
        };
        let mut stmt = conn.prepare(sql)?;
        let iter = stmt.query_map([], map_image)?;
        iter.filter_map(|r| r.ok()).collect()
    };

    if limit > 0 && limit < rows.len() as i64 {
        rows.truncate(limit as usize);
    }
    Ok(rows)
}

/// Get images pending face scan.
pub fn get_images_for_face_scan(
    pool: &DbPool,
    image_ids: &[i64],
    folder_like: Option<&str>,
    force: bool,
) -> AppResult<Vec<Image>> {
    let conn = get_conn(pool)?;
    if !image_ids.is_empty() {
        let placeholders: Vec<String> = image_ids.iter().map(|_| "?".to_string()).collect();
        let sql = format!("SELECT * FROM images WHERE id IN ({})", placeholders.join(","));
        let mut stmt = conn.prepare(&sql)?;
        let params_refs: Vec<&dyn rusqlite::ToSql> = image_ids.iter().map(|id| id as &dyn rusqlite::ToSql).collect();
        let iter = stmt.query_map(params_refs.as_slice(), map_image)?;
        return Ok(iter.filter_map(|r| r.ok()).collect());
    }

    if let Some(like) = folder_like {
        let pat = format!("{}%", like);
        if force {
            conn.execute("UPDATE images SET faces_scanned=0 WHERE path LIKE ?", params![pat])?;
            conn.execute(
                "DELETE FROM image_faces WHERE image_id IN (SELECT id FROM images WHERE path LIKE ?)",
                params![pat],
            )?;
        }
        let mut stmt = conn.prepare("SELECT * FROM images WHERE path LIKE ? AND faces_scanned=0")?;
        let iter = stmt.query_map(params![pat], map_image)?;
        return Ok(iter.filter_map(|r| r.ok()).collect());
    }

    if force {
        conn.execute("UPDATE images SET faces_scanned=0", [])?;
        conn.execute("DELETE FROM image_faces", [])?;
    }
    let mut stmt = conn.prepare("SELECT * FROM images WHERE faces_scanned=0 AND status != 'pending'")?;
    let iter = stmt.query_map([], map_image)?;
    Ok(iter.filter_map(|r| r.ok()).collect())
}


