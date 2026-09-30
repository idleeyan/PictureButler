//! Tags + image_tags CRUD (mirrors tag-related endpoints).

use rusqlite::params;

use crate::db::{get_conn, DbPool};
use crate::error::AppResult;
use crate::models::{Tag, TagWithCount};
use crate::utils::split_tags;

/// Sync comma-separated tags string into structured image_tags table.
/// Mirrors sync_image_tags in main.py.
pub fn sync_image_tags(conn: &rusqlite::Connection, image_id: i64, tags_str: &str) -> AppResult<()> {
    let names = split_tags(tags_str);
    // Clear old associations
    conn.execute("DELETE FROM image_tags WHERE image_id=?", params![image_id])?;
    for name in &names {
        if name.is_empty() {
            continue;
        }
        conn.execute("INSERT OR IGNORE INTO tags (name) VALUES (?)", params![name])?;
        let tag_id: i64 = conn.query_row(
            "SELECT id FROM tags WHERE name=?",
            params![name],
            |r| r.get(0),
        )?;
        conn.execute(
            "INSERT OR IGNORE INTO image_tags (image_id, tag_id) VALUES (?, ?)",
            params![image_id, tag_id],
        )?;
    }
    Ok(())
}

/// Backfill image_tags for legacy rows (mirrors _backfill_image_tags).
pub fn backfill_image_tags(conn: &rusqlite::Connection) -> AppResult<()> {
    let mut stmt = conn.prepare(
        "SELECT id, tags FROM images WHERE tags IS NOT NULL AND tags != ''",
    )?;
    let rows: Vec<(i64, String)> = stmt
        .query_map([], |r| Ok((r.get(0)?, r.get(1)?)))?
        .filter_map(|r| r.ok())
        .collect();
    drop(stmt);

    for (image_id, tags_str) in rows {
        // Skip if already synced
        let count: i64 = conn.query_row(
            "SELECT COUNT(*) FROM image_tags WHERE image_id=?",
            params![image_id],
            |r| r.get(0),
        )?;
        if count > 0 {
            continue;
        }
        let _ = sync_image_tags(conn, image_id, &tags_str);
    }
    Ok(())
}

/// List tags with image counts, optionally filtered by folder.
pub fn list_tags(
    pool: &DbPool,
    folder_like: Option<&str>,
    min_count: i64,
    limit: i64,
) -> AppResult<Vec<TagWithCount>> {
    let conn = get_conn(pool)?;
    let sql = if folder_like.is_some() {
        "SELECT tags.id, tags.name, COUNT(image_tags.image_id) AS count
         FROM tags
         JOIN image_tags ON image_tags.tag_id=tags.id
         JOIN images ON images.id=image_tags.image_id AND images.path LIKE ?
         GROUP BY tags.id, tags.name
         HAVING count >= ?
         ORDER BY count DESC, tags.name ASC
         LIMIT ?"
    } else {
        "SELECT tags.id, tags.name, COUNT(image_tags.image_id) AS count
         FROM tags
         JOIN image_tags ON image_tags.tag_id=tags.id
         GROUP BY tags.id, tags.name
         HAVING count >= ?
         ORDER BY count DESC, tags.name ASC
         LIMIT ?"
    };

    let mut stmt = conn.prepare(sql)?;
    let rows = if let Some(like) = folder_like {
        stmt.query_map(params![like, min_count, limit], map_tag_with_count)?
    } else {
        stmt.query_map(params![min_count, limit], map_tag_with_count)?
    };
    Ok(rows.filter_map(|r| r.ok()).collect())
}

fn map_tag_with_count(r: &rusqlite::Row) -> rusqlite::Result<TagWithCount> {
    Ok(TagWithCount {
        id: r.get(0)?,
        name: r.get(1)?,
        count: r.get(2)?,
    })
}

/// Search tags by name.
pub fn search_tags(pool: &DbPool, q: &str, limit: i64) -> AppResult<Vec<Tag>> {
    let conn = get_conn(pool)?;
    let pattern = format!("%{}%", q);
    let mut stmt = conn.prepare(
        "SELECT id, name, '' FROM tags WHERE name LIKE ? ORDER BY name LIMIT ?",
    )?;
    let rows = stmt.query_map(params![pattern, limit], |r| Ok(Tag {
        id: r.get(0)?,
        name: r.get(1)?,
        created_at: r.get(2)?,
    }))?;
    Ok(rows.filter_map(|r| r.ok()).collect())
}

/// Delete a tag and clean up associations.
pub fn delete_tag(pool: &DbPool, tag_id: i64) -> AppResult<String> {
    let conn = get_conn(pool)?;
    let name: String = conn
        .query_row("SELECT name FROM tags WHERE id=?", params![tag_id], |r| r.get(0))
        .map_err(|_| crate::error::AppError::not_found("Tag not found"))?;
    conn.execute("DELETE FROM image_tags WHERE tag_id=?", params![tag_id])?;
    conn.execute("DELETE FROM tags WHERE id=?", params![tag_id])?;
    // Remove from images.tags string
    let mut stmt = conn.prepare("SELECT id, tags FROM images WHERE tags LIKE ?")?;
    let rows: Vec<(i64, String)> = stmt
        .query_map(params![format!("%{}%", name)], |r| Ok((r.get(0)?, r.get(1)?)))?
        .filter_map(|r| r.ok())
        .collect();
    drop(stmt);
    for (img_id, tags_str) in rows {
        let new_tags: Vec<String> = split_tags(&tags_str)
            .into_iter()
            .filter(|t| t != &name)
            .collect();
        conn.execute(
            "UPDATE images SET tags=? WHERE id=?",
            params![new_tags.join(","), img_id],
        )?;
    }
    Ok(name)
}

/// Merge multiple source tags into a target tag.
pub fn merge_tags(pool: &DbPool, sources: &[String], target_name: &str) -> AppResult<()> {
    let conn = get_conn(pool)?;
    conn.execute("INSERT OR IGNORE INTO tags (name) VALUES (?)", params![target_name])?;
    let target_id: i64 = conn.query_row(
        "SELECT id FROM tags WHERE name=?",
        params![target_name],
        |r| r.get(0),
    )?;

    for sn in sources {
        let s_id: Option<i64> = conn
            .query_row("SELECT id FROM tags WHERE name=?", params![sn], |r| r.get(0))
            .ok();
        let Some(s_id) = s_id else { continue };
        // Migrate image_tags
        let mut stmt = conn.prepare("SELECT image_id FROM image_tags WHERE tag_id=?")?;
        let image_ids: Vec<i64> = stmt
            .query_map(params![s_id], |r| r.get(0))?
            .filter_map(|r| r.ok())
            .collect();
        drop(stmt);
        for img_id in image_ids {
            conn.execute(
                "INSERT OR IGNORE INTO image_tags (image_id, tag_id) VALUES (?, ?)",
                params![img_id, target_id],
            )?;
        }
        conn.execute("DELETE FROM image_tags WHERE tag_id=?", params![s_id])?;
        conn.execute("DELETE FROM tags WHERE id=?", params![s_id])?;
    }

    // Regenerate images.tags strings
    let mut stmt = conn.prepare(
        "SELECT id FROM images WHERE id IN (SELECT DISTINCT image_id FROM image_tags)",
    )?;
    let img_ids: Vec<i64> = stmt.query_map([], |r| r.get(0))?.filter_map(|r| r.ok()).collect();
    drop(stmt);
    for img_id in img_ids {
        let mut tag_stmt = conn.prepare(
            "SELECT tags.name FROM image_tags JOIN tags ON tags.id=image_tags.tag_id WHERE image_tags.image_id=?",
        )?;
        let names: Vec<String> = tag_stmt
            .query_map(params![img_id], |r| r.get(0))?
            .filter_map(|r| r.ok())
            .collect();
        drop(tag_stmt);
        conn.execute(
            "UPDATE images SET tags=? WHERE id=?",
            params![names.join(","), img_id],
        )?;
    }
    Ok(())
}
