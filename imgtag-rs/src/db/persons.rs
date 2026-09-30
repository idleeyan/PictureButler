//! Persons + image_faces CRUD (mirrors person/face endpoints in main.py).

use rusqlite::params;
use std::collections::HashMap;

use crate::db::{get_conn, DbPool};
use crate::error::{AppError, AppResult};
use crate::models::{ImageFace, ImageFaceWithPerson, Person, PersonWithStats};
use crate::utils::next_person_name;

/// Embedding dimension (ArcFace 512-d).
pub const EMB_DIM: usize = 512;

/// Serialize an embedding to raw bytes (little-endian f32).
pub fn embedding_to_bytes(emb: &[f32]) -> Vec<u8> {
    let mut bytes = Vec::with_capacity(emb.len() * 4);
    for &v in emb {
        bytes.extend_from_slice(&v.to_le_bytes());
    }
    bytes
}

/// Deserialize an embedding from raw bytes.
/// Returns None if the blob is not a valid raw embedding (e.g., legacy pickle format).
pub fn bytes_to_embedding(blob: &[u8]) -> Option<Vec<f32>> {
    if blob.len() == EMB_DIM * 4 {
        // Raw f32 little-endian format
        let mut emb = Vec::with_capacity(EMB_DIM);
        for chunk in blob.chunks_exact(4) {
            emb.push(f32::from_le_bytes([chunk[0], chunk[1], chunk[2], chunk[3]]));
        }
        Some(emb)
    } else if blob.len() > 0 && blob[0] == 0x80 {
        // Pickle protocol header — legacy format, skip (run migration script)
        None
    } else {
        None
    }
}

/// List all persons with stats, optionally filtered by folder.
pub fn list_persons(
    pool: &DbPool,
    folder_like: Option<&str>,
    include_ignored: bool,
) -> AppResult<Vec<PersonWithStats>> {
    let conn = get_conn(pool)?;
    let folder_cond = if folder_like.is_some() {
        "AND images.path LIKE ?"
    } else {
        ""
    };
    let ignored_cond = if include_ignored { "" } else { "AND persons.ignored = 0" };
    let sql = format!(
        "SELECT persons.id, persons.name, persons.avatar_face_id, persons.ignored,
                COUNT(DISTINCT image_faces.image_id) AS image_count,
                COUNT(image_faces.id) AS face_count,
                persons.created_at
         FROM persons
         LEFT JOIN image_faces ON image_faces.person_id = persons.id
         LEFT JOIN images ON images.id = image_faces.image_id {}
         WHERE 1=1 {}
         GROUP BY persons.id
         HAVING image_count > 0
         ORDER BY persons.ignored ASC, image_count DESC, persons.id",
        folder_cond, ignored_cond
    );

    let mut stmt = conn.prepare(&sql)?;
    let rows = if let Some(like) = folder_like {
        let pat = format!("{}%", like);
        stmt.query_map(params![pat], map_person_with_stats)?
    } else {
        stmt.query_map([], map_person_with_stats)?
    };
    Ok(rows.filter_map(|r| r.ok()).collect())
}

fn map_person_with_stats(r: &rusqlite::Row) -> rusqlite::Result<PersonWithStats> {
    Ok(PersonWithStats {
        id: r.get(0)?,
        name: r.get(1)?,
        avatar_face_id: r.get(2)?,
        ignored: r.get(3)?,
        image_count: r.get(4)?,
        face_count: r.get(5)?,
        created_at: r.get(6)?,
    })
}

/// Get a single person with stats.
pub fn get_person(pool: &DbPool, person_id: i64) -> AppResult<PersonWithStats> {
    let conn = get_conn(pool)?;
    let p = conn
        .query_row("SELECT id, name, avatar_face_id, ignored, created_at FROM persons WHERE id=?", params![person_id], |r| {
            Ok(Person {
                id: r.get(0)?,
                name: r.get(1)?,
                avatar_face_id: r.get(2)?,
                ignored: r.get(3)?,
                created_at: r.get(4)?,
            })
        })
        .map_err(|_| AppError::not_found("Person not found"))?;
    let image_count: i64 = conn.query_row(
        "SELECT COUNT(DISTINCT image_id) FROM image_faces WHERE person_id=?",
        params![person_id], |r| r.get(0),
    )?;
    let face_count: i64 = conn.query_row(
        "SELECT COUNT(*) FROM image_faces WHERE person_id=?",
        params![person_id], |r| r.get(0),
    )?;
    Ok(PersonWithStats {
        id: p.id, name: p.name, avatar_face_id: p.avatar_face_id, ignored: p.ignored,
        image_count, face_count, created_at: p.created_at,
    })
}

/// Get images containing a person (paginated).
pub fn get_person_images(
    pool: &DbPool,
    person_id: i64,
    page: i64,
    per_page: i64,
) -> AppResult<(i64, Vec<crate::models::Image>)> {
    let conn = get_conn(pool)?;
    let total: i64 = conn.query_row(
        "SELECT COUNT(DISTINCT image_id) FROM image_faces WHERE person_id=?",
        params![person_id], |r| r.get(0),
    )?;
    let offset = (page - 1) * per_page;
    let mut stmt = conn.prepare(
        "SELECT DISTINCT images.* FROM images
         JOIN image_faces ON image_faces.image_id = images.id
         WHERE image_faces.person_id = ?
         ORDER BY images.date_taken DESC, images.id DESC
         LIMIT ? OFFSET ?",
    )?;
    let rows = stmt.query_map(params![person_id, per_page, offset], |r| {
        Ok(crate::models::Image {
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
    })?;
    let images: Vec<_> = rows.filter_map(|r| r.ok()).collect();
    Ok((total, images))
}

/// Create a new person with auto-generated name.
pub fn create_person(conn: &rusqlite::Connection) -> AppResult<i64> {
    // Get existing names
    let mut stmt = conn.prepare("SELECT name FROM persons WHERE name LIKE '人物%'")?;
    let names: Vec<String> = stmt.query_map([], |r| r.get(0))?.filter_map(|r| r.ok()).collect();
    drop(stmt);
    let name = next_person_name(&names);
    conn.execute("INSERT INTO persons (name) VALUES (?)", params![name])?;
    Ok(conn.last_insert_rowid())
}

/// Insert a face record.
pub fn insert_face(
    conn: &rusqlite::Connection,
    image_id: i64,
    person_id: i64,
    bbox: &str,
    embedding: &[f32],
    score: f64,
) -> AppResult<i64> {
    let emb_blob = embedding_to_bytes(embedding);
    conn.execute(
        "INSERT INTO image_faces (image_id, person_id, bbox, embedding, score) VALUES (?, ?, ?, ?, ?)",
        params![image_id, person_id, bbox, emb_blob, score],
    )?;
    Ok(conn.last_insert_rowid())
}

/// Set avatar for a person if not already set.
pub fn set_avatar_if_needed(conn: &rusqlite::Connection, person_id: i64, face_id: i64) -> AppResult<()> {
    let avatar: Option<i64> = conn
        .query_row("SELECT avatar_face_id FROM persons WHERE id=?", params![person_id], |r| r.get(0))
        .ok()
        .flatten();
    if avatar.is_none() {
        conn.execute(
            "UPDATE persons SET avatar_face_id=? WHERE id=?",
            params![face_id, person_id],
        )?;
    }
    Ok(())
}

/// Match an embedding against all known persons' faces.
/// Returns the best-matching person_id if similarity >= threshold.
pub fn match_person(
    conn: &rusqlite::Connection,
    embedding: &[f32],
    threshold: f64,
) -> AppResult<Option<i64>> {
    let mut stmt = conn.prepare(
        "SELECT id, embedding, person_id FROM image_faces WHERE person_id IS NOT NULL AND embedding IS NOT NULL",
    )?;
    let rows: Vec<(i64, Vec<u8>, i64)> = stmt
        .query_map([], |r| {
            let emb_blob: Vec<u8> = r.get(1)?;
            Ok((r.get(0)?, emb_blob, r.get(2)?))
        })?
        .filter_map(|r| r.ok())
        .collect();
    drop(stmt);

    let mut best_pid: Option<i64> = None;
    let mut best_sim = 0.0f64;
    for (_, blob, pid) in rows {
        let Some(emb) = bytes_to_embedding(&blob) else { continue };
        let sim = cosine_sim(embedding, &emb);
        if sim > best_sim {
            best_sim = sim;
            best_pid = Some(pid);
        }
    }
    if best_sim >= threshold {
        Ok(best_pid)
    } else {
        Ok(None)
    }
}

/// Cosine similarity between two vectors.
pub fn cosine_sim(a: &[f32], b: &[f32]) -> f64 {
    let dot: f64 = a.iter().zip(b.iter()).map(|(x, y)| (*x as f64) * (*y as f64)).sum();
    let na: f64 = a.iter().map(|x| (*x as f64).powi(2)).sum::<f64>().sqrt();
    let nb: f64 = b.iter().map(|x| (*x as f64).powi(2)).sum::<f64>().sqrt();
    if na < 1e-6 || nb < 1e-6 {
        return 0.0;
    }
    dot / (na * nb)
}

/// Get merge suggestions for a person (image_count <= 5).
pub fn get_suggestions(
    pool: &DbPool,
    person_id: i64,
    max_results: usize,
) -> AppResult<serde_json::Value> {
    let conn = get_conn(pool)?;
    let target = conn
        .query_row("SELECT id, name, avatar_face_id FROM persons WHERE id=?", params![person_id], |r| {
            Ok((r.get::<_, i64>(0)?, r.get::<_, String>(1)?, r.get::<_, Option<i64>>(2)?))
        })
        .map_err(|_| AppError::not_found("Person not found"))?;
    let target_face_count: i64 = conn.query_row(
        "SELECT COUNT(*) FROM image_faces WHERE person_id=?", params![person_id], |r| r.get(0),
    )?;
    let target_image_count: i64 = conn.query_row(
        "SELECT COUNT(DISTINCT image_id) FROM image_faces WHERE person_id=?", params![person_id], |r| r.get(0),
    )?;

    if target_image_count > 5 {
        return Ok(serde_json::json!({
            "target": {
                "id": target.0, "name": target.1,
                "image_count": target_image_count, "face_count": target_face_count,
                "avatar_face_id": target.2, "face_ids": [],
            },
            "suggestions": [],
            "reason": "Image count > 5, no merge suggestions",
        }));
    }

    // Load target embeddings
    let mut stmt = conn.prepare("SELECT id, embedding FROM image_faces WHERE person_id=? AND embedding IS NOT NULL")?;
    let target_rows: Vec<(i64, Vec<u8>)> = stmt.query_map(params![person_id], |r| {
        Ok((r.get(0)?, r.get(1)?))
    })?.filter_map(|r| r.ok()).collect();
    drop(stmt);

    let mut target_embs: Vec<Vec<f32>> = Vec::new();
    let mut target_face_ids: Vec<i64> = Vec::new();
    for (fid, blob) in &target_rows {
        if let Some(emb) = bytes_to_embedding(blob) {
            target_embs.push(emb);
            target_face_ids.push(*fid);
        }
    }

    if target_embs.is_empty() {
        return Ok(serde_json::json!({
            "target": {
                "id": target.0, "name": target.1,
                "image_count": target_image_count, "face_count": target_face_count,
                "avatar_face_id": target.2, "face_ids": [],
            },
            "suggestions": [],
        }));
    }

    // Load all other persons' faces
    let mut stmt = conn.prepare(
        "SELECT image_faces.id, image_faces.person_id, image_faces.embedding, persons.name
         FROM image_faces JOIN persons ON persons.id = image_faces.person_id
         WHERE image_faces.person_id != ? AND image_faces.embedding IS NOT NULL AND persons.ignored = 0",
    )?;
    let other_rows: Vec<(i64, i64, Vec<u8>, String)> = stmt
        .query_map(params![person_id], |r| {
            Ok((r.get(0)?, r.get(1)?, r.get(2)?, r.get(3)?))
        })?
        .filter_map(|r| r.ok())
        .collect();
    drop(stmt);

    // Group by person_id
    let mut groups: HashMap<i64, (String, Vec<Vec<f32>>, Vec<i64>)> = HashMap::new();
    for (fid, pid, blob, name) in other_rows {
        if let Some(emb) = bytes_to_embedding(&blob) {
            let entry = groups.entry(pid).or_insert_with(|| (name, vec![], vec![]));
            entry.1.push(emb);
            entry.2.push(fid);
        }
    }

    // Compute candidates
    let mut candidates: Vec<serde_json::Value> = Vec::new();
    for (pid, (name, embs, face_ids)) in &groups {
        let mut sims = Vec::new();
        for te in &target_embs {
            for oe in embs {
                sims.push(cosine_sim(te, oe));
            }
        }
        if sims.is_empty() { continue; }
        let max_sim = sims.iter().cloned().fold(0.0f64, f64::max);
        let avg_sim = sims.iter().sum::<f64>() / sims.len() as f64;
        if max_sim < 0.4 { continue; }

        let probability = if max_sim >= 0.9 {
            99
        } else if max_sim >= 0.5 {
            (50.0 + (max_sim - 0.5) * (49.0 / 0.4)) as i64
        } else {
            ((max_sim - 0.4) * 500.0) as i64
        };

        let pinfo = conn.query_row("SELECT name, avatar_face_id FROM persons WHERE id=?", params![pid], |r| {
            Ok((r.get::<_, String>(0)?, r.get::<_, Option<i64>>(1)?))
        }).ok();
        let img_cnt: i64 = conn.query_row(
            "SELECT COUNT(DISTINCT image_id) FROM image_faces WHERE person_id=?",
            params![pid], |r| r.get(0),
        ).unwrap_or(0);

        let candidate_face_ids: Vec<i64> = face_ids.iter().take(4).copied().collect();
        candidates.push(serde_json::json!({
            "person_id": pid,
            "name": pinfo.as_ref().map(|(n, _)| n.clone()).unwrap_or_else(|| name.clone()),
            "image_count": img_cnt,
            "avatar_face_id": pinfo.and_then(|(_, a)| a),
            "face_ids": candidate_face_ids,
            "max_sim": (max_sim * 10000.0).round() / 10000.0,
            "avg_sim": (avg_sim * 10000.0).round() / 10000.0,
            "probability": probability.clamp(0, 99),
        }));
    }

    // Sort by max_sim descending
    candidates.sort_by(|a, b| {
        let a_sim = a["max_sim"].as_f64().unwrap_or(0.0);
        let b_sim = b["max_sim"].as_f64().unwrap_or(0.0);
        b_sim.partial_cmp(&a_sim).unwrap_or(std::cmp::Ordering::Equal)
    });
    candidates.truncate(max_results);

    let target_face_ids_trunc: Vec<i64> = target_face_ids.iter().take(4).copied().collect();
    Ok(serde_json::json!({
        "target": {
            "id": target.0, "name": target.1,
            "image_count": target_image_count, "face_count": target_face_count,
            "avatar_face_id": target.2, "face_ids": target_face_ids_trunc,
        },
        "suggestions": candidates,
    }))
}

/// Update person: rename, change avatar, or set ignored.
pub fn update_person(
    pool: &DbPool,
    person_id: i64,
    name: Option<&str>,
    avatar_face_id: Option<i64>,
    ignored: Option<bool>,
) -> AppResult<()> {
    let conn = get_conn(pool)?;
    let exists: bool = conn
        .query_row("SELECT 1 FROM persons WHERE id=?", params![person_id], |_| Ok(true))
        .unwrap_or(false);
    if !exists {
        return Err(AppError::not_found("Person not found"));
    }
    if let Some(n) = name {
        if n.is_empty() {
            return Err(AppError::bad_request("Name cannot be empty"));
        }
        conn.execute("UPDATE persons SET name=? WHERE id=?", params![n, person_id])?;
    }
    if let Some(av_id) = avatar_face_id {
        let valid: bool = conn
            .query_row(
                "SELECT 1 FROM image_faces WHERE id=? AND person_id=?",
                params![av_id, person_id],
                |_| Ok(true),
            )
            .unwrap_or(false);
        if !valid {
            return Err(AppError::bad_request("Face does not belong to this person"));
        }
        conn.execute("UPDATE persons SET avatar_face_id=? WHERE id=?", params![av_id, person_id])?;
    }
    if let Some(ig) = ignored {
        conn.execute("UPDATE persons SET ignored=? WHERE id=?", params![if ig { 1 } else { 0 }, person_id])?;
    }
    Ok(())
}

/// Merge multiple persons into target.
pub fn merge_persons(pool: &DbPool, source_ids: &[i64], target_id: i64) -> AppResult<()> {
    let conn = get_conn(pool)?;
    let target_exists: bool = conn
        .query_row("SELECT 1 FROM persons WHERE id=?", params![target_id], |_| Ok(true))
        .unwrap_or(false);
    if !target_exists {
        return Err(AppError::not_found("Target person not found"));
    }
    for &sid in source_ids {
        if sid == target_id { continue; }
        let exists: bool = conn
            .query_row("SELECT 1 FROM persons WHERE id=?", params![sid], |_| Ok(true))
            .unwrap_or(false);
        if !exists { continue; }
        // Migrate faces
        conn.execute("UPDATE image_faces SET person_id=? WHERE person_id=?", params![target_id, sid])?;
        // Transfer avatar if target has none
        let target_avatar: Option<i64> = conn
            .query_row("SELECT avatar_face_id FROM persons WHERE id=?", params![target_id], |r| r.get(0))
            .ok()
            .flatten();
        if target_avatar.is_none() {
            let source_avatar: Option<i64> = conn
                .query_row("SELECT avatar_face_id FROM persons WHERE id=?", params![sid], |r| r.get(0))
                .ok()
                .flatten();
            if let Some(av) = source_avatar {
                conn.execute("UPDATE persons SET avatar_face_id=? WHERE id=?", params![av, target_id])?;
            }
        }
        // Delete source
        conn.execute("DELETE FROM persons WHERE id=?", params![sid])?;
    }
    Ok(())
}

/// Delete a person.
pub fn delete_person(pool: &DbPool, person_id: i64, delete_faces: bool) -> AppResult<()> {
    let conn = get_conn(pool)?;
    let exists: bool = conn
        .query_row("SELECT 1 FROM persons WHERE id=?", params![person_id], |_| Ok(true))
        .unwrap_or(false);
    if !exists {
        return Err(AppError::not_found("Person not found"));
    }
    if delete_faces {
        let affected: Vec<i64> = {
            let mut stmt = conn.prepare("SELECT DISTINCT image_id FROM image_faces WHERE person_id=?")?;
            let iter = stmt.query_map(params![person_id], |r| r.get(0))?;
            iter.filter_map(|r| r.ok()).collect()
        };
        conn.execute("DELETE FROM image_faces WHERE person_id=?", params![person_id])?;
        for img_id in affected {
            conn.execute("UPDATE images SET faces_scanned=0 WHERE id=?", params![img_id])?;
        }
    }
    conn.execute("DELETE FROM persons WHERE id=?", params![person_id])?;
    Ok(())
}

/// Batch person operations: ignore/unignore/delete.
pub fn batch_persons(pool: &DbPool, ids: &[i64], action: &str, delete_faces: bool) -> AppResult<(i64, Vec<serde_json::Value>)> {
    let conn = get_conn(pool)?;
    let mut affected = 0i64;
    let mut failures: Vec<serde_json::Value> = Vec::new();
    for &pid in ids {
        let exists: bool = conn
            .query_row("SELECT 1 FROM persons WHERE id=?", params![pid], |_| Ok(true))
            .unwrap_or(false);
        if !exists {
            failures.push(serde_json::json!({"id": pid, "error": "Person not found"}));
            continue;
        }
        match action {
            "ignore" => {
                conn.execute("UPDATE persons SET ignored=1 WHERE id=?", params![pid])?;
                affected += 1;
            }
            "unignore" => {
                conn.execute("UPDATE persons SET ignored=0 WHERE id=?", params![pid])?;
                affected += 1;
            }
            "delete" => {
                if delete_faces {
                    let affected_imgs: Vec<i64> = {
                        let mut stmt = conn.prepare("SELECT DISTINCT image_id FROM image_faces WHERE person_id=?")?;
                        let iter = stmt.query_map(params![pid], |r| r.get(0))?;
                        iter.filter_map(|r| r.ok()).collect()
                    };
                    conn.execute("DELETE FROM image_faces WHERE person_id=?", params![pid])?;
                    for img_id in affected_imgs {
                        conn.execute("UPDATE images SET faces_scanned=0 WHERE id=?", params![img_id])?;
                    }
                }
                conn.execute("DELETE FROM persons WHERE id=?", params![pid])?;
                affected += 1;
            }
            _ => {
                failures.push(serde_json::json!({"id": pid, "error": "Invalid action"}));
            }
        }
    }
    Ok((affected, failures))
}

/// List faces for a person.
pub fn list_person_faces(pool: &DbPool, person_id: i64, limit: i64) -> AppResult<Vec<ImageFace>> {
    let conn = get_conn(pool)?;
    let mut stmt = conn.prepare(
        "SELECT id, image_id, person_id, bbox, score, created_at FROM image_faces
         WHERE person_id=? ORDER BY score DESC LIMIT ?",
    )?;
    let rows = stmt.query_map(params![person_id, limit], |r| {
        Ok(ImageFace {
            id: r.get(0)?,
            image_id: r.get(1)?,
            person_id: r.get(2)?,
            bbox: r.get(3)?,
            score: r.get(4)?,
            created_at: r.get(5).unwrap_or_default(),
        })
    })?;
    Ok(rows.filter_map(|r| r.ok()).collect())
}

/// Get a face's image_id and bbox (for thumbnail generation).
pub fn get_face_info(pool: &DbPool, face_id: i64) -> AppResult<(i64, String)> {
    let conn = get_conn(pool)?;
    let row = conn
        .query_row("SELECT image_id, bbox FROM image_faces WHERE id=?", params![face_id], |r| {
            Ok((r.get::<_, i64>(0)?, r.get::<_, String>(1)?))
        })
        .map_err(|_| AppError::not_found("Face not found"))?;
    Ok(row)
}

/// Get faces for an image (with person info).
pub fn get_image_faces(pool: &DbPool, image_id: i64) -> AppResult<Vec<ImageFaceWithPerson>> {
    let conn = get_conn(pool)?;
    let mut stmt = conn.prepare(
        "SELECT image_faces.id, image_faces.bbox, image_faces.score,
                image_faces.person_id, persons.name AS person_name
         FROM image_faces
         LEFT JOIN persons ON persons.id = image_faces.person_id
         WHERE image_faces.image_id=?
         ORDER BY image_faces.score DESC",
    )?;
    let rows = stmt.query_map(params![image_id], |r| {
        Ok(ImageFaceWithPerson {
            id: r.get(0)?,
            bbox: r.get(1)?,
            score: r.get(2)?,
            person_id: r.get(3)?,
            person_name: r.get(4)?,
        })
    })?;
    Ok(rows.filter_map(|r| r.ok()).collect())
}

/// Reassign a face to a different person.
pub fn reassign_face(pool: &DbPool, face_id: i64, person_id: Option<i64>, new_name: Option<&str>) -> AppResult<i64> {
    let conn = get_conn(pool)?;
    let exists: bool = conn
        .query_row("SELECT 1 FROM image_faces WHERE id=?", params![face_id], |_| Ok(true))
        .unwrap_or(false);
    if !exists {
        return Err(AppError::not_found("Face not found"));
    }
    let mut final_pid = person_id;
    if let Some(name) = new_name {
        if person_id.is_none() {
            // Create new person
            let name = name.trim();
            conn.execute("INSERT INTO persons (name) VALUES (?)", params![name])?;
            final_pid = Some(conn.last_insert_rowid());
        }
    }
    if let Some(pid) = final_pid {
        let valid: bool = conn
            .query_row("SELECT 1 FROM persons WHERE id=?", params![pid], |_| Ok(true))
            .unwrap_or(false);
        if !valid {
            return Err(AppError::not_found("Target person not found"));
        }
        conn.execute("UPDATE image_faces SET person_id=? WHERE id=?", params![pid, face_id])?;
    } else {
        conn.execute("UPDATE image_faces SET person_id=NULL WHERE id=?", params![face_id])?;
    }
    Ok(final_pid.unwrap_or(0))
}

/// Load all embeddings for reclustering.
pub fn load_all_embeddings(pool: &DbPool) -> AppResult<Vec<(i64, Vec<f32>)>> {
    let conn = get_conn(pool)?;
    let mut stmt = conn.prepare("SELECT id, embedding FROM image_faces WHERE embedding IS NOT NULL")?;
    let rows: Vec<(i64, Vec<u8>)> = stmt.query_map([], |r| {
        Ok((r.get(0)?, r.get(1)?))
    })?.filter_map(|r| r.ok()).collect();
    drop(stmt);
    let mut result = Vec::new();
    for (id, blob) in rows {
        if let Some(emb) = bytes_to_embedding(&blob) {
            result.push((id, emb));
        }
    }
    Ok(result)
}

/// Load named persons (seeds for reclustering).
pub fn load_named_persons(pool: &DbPool) -> AppResult<Vec<(i64, String, Option<i64>)>> {
    let conn = get_conn(pool)?;
    let mut stmt = conn.prepare(
        "SELECT id, name, avatar_face_id FROM persons
         WHERE name NOT GLOB '人物[0-9]*' OR ignored = 1",
    )?;
    let rows = stmt.query_map([], |r| {
        Ok((r.get::<_, i64>(0)?, r.get::<_, String>(1)?, r.get::<_, Option<i64>>(2)?))
    })?;
    Ok(rows.filter_map(|r| r.ok()).collect())
}

/// Clear all person assignments (for reclustering).
pub fn clear_all_person_assignments(conn: &rusqlite::Connection) -> AppResult<()> {
    conn.execute("UPDATE image_faces SET person_id=NULL", [])?;
    Ok(())
}

/// Delete unnamed persons (for reclustering).
pub fn delete_unnamed_persons(conn: &rusqlite::Connection, named_ids: &[i64]) -> AppResult<()> {
    if named_ids.is_empty() {
        conn.execute("DELETE FROM persons", [])?;
    } else {
        let placeholders: Vec<String> = named_ids.iter().map(|_| "?".to_string()).collect();
        let sql = format!("DELETE FROM persons WHERE id NOT IN ({})", placeholders.join(","));
        let params_refs: Vec<&dyn rusqlite::ToSql> = named_ids.iter().map(|id| id as &dyn rusqlite::ToSql).collect();
        conn.execute(&sql, params_refs.as_slice())?;
    }
    Ok(())
}

/// Update face's person assignment.
pub fn update_face_person(conn: &rusqlite::Connection, face_id: i64, person_id: i64) -> AppResult<()> {
    conn.execute("UPDATE image_faces SET person_id=? WHERE id=?", params![person_id, face_id])?;
    Ok(())
}

/// Set person avatar.
pub fn set_person_avatar(conn: &rusqlite::Connection, person_id: i64, face_id: i64) -> AppResult<()> {
    conn.execute("UPDATE persons SET avatar_face_id=? WHERE id=?", params![face_id, person_id])?;
    Ok(())
}
