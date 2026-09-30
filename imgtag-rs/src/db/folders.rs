//! Folders CRUD (mirrors /api/folders endpoints).

use rusqlite::params;

use crate::db::{get_conn, DbPool};
use crate::error::{AppError, AppResult};
use crate::models::{Folder, FolderWithCount};

pub fn list_folders(pool: &DbPool) -> AppResult<Vec<FolderWithCount>> {
    let conn = get_conn(pool)?;
    let mut stmt = conn.prepare(
        "SELECT f.id, f.path, f.alias, f.created_at,
                (SELECT COUNT(*) FROM images WHERE path LIKE f.path || '%') AS image_count
         FROM folders f ORDER BY f.id",
    )?;
    let rows = stmt.query_map([], |r| {
        Ok(FolderWithCount {
            id: r.get(0)?,
            path: r.get(1)?,
            alias: r.get(2)?,
            created_at: r.get(3)?,
            image_count: r.get(4)?,
        })
    })?;
    Ok(rows.filter_map(|r| r.ok()).collect())
}

pub fn add_folder(pool: &DbPool, path: &str, alias: &str) -> AppResult<Folder> {
    let conn = get_conn(pool)?;
    let alias = alias.trim();
    // Try insert; on conflict, update alias if provided
    let result = conn.execute(
        "INSERT INTO folders (path, alias) VALUES (?, ?)",
        params![path, alias],
    );
    if result.is_err() {
        // Likely unique constraint violation; update alias if non-empty
        if !alias.is_empty() {
            conn.execute(
                "UPDATE folders SET alias=? WHERE path=?",
                params![alias, path],
            )?;
        }
    }
    let row = conn.query_row(
        "SELECT id, path, alias FROM folders WHERE path=?",
        params![path],
        |r| Ok(Folder {
            id: r.get(0)?,
            path: r.get(1)?,
            alias: r.get(2)?,
            created_at: String::new(),
        }),
    )?;
    Ok(row)
}

pub fn update_folder_alias(pool: &DbPool, folder_id: i64, alias: &str) -> AppResult<()> {
    let conn = get_conn(pool)?;
    let n = conn.execute(
        "UPDATE folders SET alias=? WHERE id=?",
        params![alias, folder_id],
    )?;
    if n == 0 {
        return Err(AppError::not_found("Folder not found"));
    }
    Ok(())
}

pub fn delete_folder(pool: &DbPool, folder_id: i64) -> AppResult<()> {
    let conn = get_conn(pool)?;
    let folder_path: Option<String> = conn
        .query_row(
            "SELECT path FROM folders WHERE id=?",
            params![folder_id],
            |r| r.get(0),
        )
        .ok();
    let Some(folder_path) = folder_path else {
        return Err(AppError::not_found("Folder not found"));
    };
    let like = format!("{}%", folder_path);
    conn.execute("DELETE FROM images WHERE path LIKE ?", params![like])?;
    conn.execute("DELETE FROM folders WHERE id=?", params![folder_id])?;
    Ok(())
}
