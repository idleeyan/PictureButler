//! Schema migrations for legacy tables (mirrors _migrate_*_table in main.py).

use rusqlite::Connection;

use crate::error::AppResult;

/// Get existing column names of a table.
fn existing_columns(conn: &Connection, table: &str) -> Vec<String> {
    let mut stmt = match conn.prepare(&format!("PRAGMA table_info({})", table)) {
        Ok(s) => s,
        Err(_) => return vec![],
    };
    let rows = stmt
        .query_map([], |r| r.get::<_, String>(1))
        .ok()
        .map(|rows| rows.filter_map(|r| r.ok()).collect())
        .unwrap_or_default();
    rows
}

/// Add missing columns to legacy images table.
pub fn migrate_images_table(conn: &Connection) -> AppResult<()> {
    let cols = existing_columns(conn, "images");
    let additions = [
        ("width", "INTEGER DEFAULT 0"),
        ("height", "INTEGER DEFAULT 0"),
        ("file_size", "INTEGER DEFAULT 0"),
        ("date_taken", "TEXT DEFAULT ''"),
        ("faces_scanned", "INTEGER DEFAULT 0"),
    ];
    for (col, decl) in &additions {
        if !cols.iter().any(|c| c == col) {
            conn.execute(&format!("ALTER TABLE images ADD COLUMN {} {}", col, decl), [])?;
        }
    }
    Ok(())
}

/// Add alias column to legacy folders table.
pub fn migrate_folders_table(conn: &Connection) -> AppResult<()> {
    let cols = existing_columns(conn, "folders");
    if !cols.iter().any(|c| c == "alias") {
        conn.execute("ALTER TABLE folders ADD COLUMN alias TEXT DEFAULT ''", [])?;
    }
    Ok(())
}

/// Add ignored column to legacy persons table.
pub fn migrate_persons_table(conn: &Connection) -> AppResult<()> {
    let cols = existing_columns(conn, "persons");
    if !cols.iter().any(|c| c == "ignored") {
        conn.execute("ALTER TABLE persons ADD COLUMN ignored INTEGER DEFAULT 0", [])?;
    }
    Ok(())
}

/// Run all migrations.
pub fn migrate_all(conn: &Connection) -> AppResult<()> {
    migrate_images_table(conn)?;
    migrate_folders_table(conn)?;
    migrate_persons_table(conn)?;
    Ok(())
}
