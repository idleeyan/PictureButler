//! Database schema: CREATE TABLE statements + indexes + triggers.
//!
//! Mirrors the SQL in main.py init_db().

use rusqlite::Connection;

use crate::error::AppResult;

/// Create all tables if they don't exist.
pub fn create_tables(conn: &Connection) -> AppResult<()> {
    conn.execute_batch(
        "
        CREATE TABLE IF NOT EXISTS images (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            path TEXT UNIQUE NOT NULL,
            filename TEXT NOT NULL,
            description TEXT DEFAULT '',
            tags TEXT DEFAULT '',
            status TEXT DEFAULT 'pending',
            width INTEGER DEFAULT 0,
            height INTEGER DEFAULT 0,
            file_size INTEGER DEFAULT 0,
            date_taken TEXT DEFAULT '',
            faces_scanned INTEGER DEFAULT 0,
            created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
        );

        CREATE TABLE IF NOT EXISTS folders (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            path TEXT UNIQUE NOT NULL,
            alias TEXT DEFAULT '',
            created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
        );

        CREATE TABLE IF NOT EXISTS tags (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            name TEXT UNIQUE NOT NULL,
            created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
        );

        CREATE TABLE IF NOT EXISTS image_tags (
            image_id INTEGER NOT NULL,
            tag_id INTEGER NOT NULL,
            PRIMARY KEY (image_id, tag_id),
            FOREIGN KEY (image_id) REFERENCES images(id) ON DELETE CASCADE,
            FOREIGN KEY (tag_id) REFERENCES tags(id) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS persons (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            name TEXT NOT NULL,
            avatar_face_id INTEGER,
            ignored INTEGER DEFAULT 0,
            created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
        );

        CREATE TABLE IF NOT EXISTS image_faces (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            image_id INTEGER NOT NULL,
            person_id INTEGER,
            bbox TEXT NOT NULL,
            embedding BLOB,
            score REAL DEFAULT 0,
            created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
            FOREIGN KEY (image_id) REFERENCES images(id) ON DELETE CASCADE,
            FOREIGN KEY (person_id) REFERENCES persons(id) ON DELETE SET NULL
        );

        CREATE TABLE IF NOT EXISTS settings (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        ",
    )?;
    Ok(())
}

/// Create indexes and FTS5 virtual table + triggers.
pub fn create_indexes_and_triggers(conn: &Connection) -> AppResult<()> {
    conn.execute_batch(
        "
        CREATE INDEX IF NOT EXISTS idx_image_tags_tag ON image_tags(tag_id);
        CREATE INDEX IF NOT EXISTS idx_image_faces_person ON image_faces(person_id);
        CREATE INDEX IF NOT EXISTS idx_image_faces_image ON image_faces(image_id);

        CREATE VIRTUAL TABLE IF NOT EXISTS images_fts USING fts5(
            filename, description, tags, content='images', content_rowid='id'
        );
        ",
    )?;

    // Triggers to keep FTS in sync (must use executescript for multiple statements)
    conn.execute_batch(
        "
        CREATE TRIGGER IF NOT EXISTS images_ai AFTER INSERT ON images BEGIN
            INSERT INTO images_fts(rowid, filename, description, tags)
            VALUES (new.id, new.filename, new.description, new.tags);
        END;
        CREATE TRIGGER IF NOT EXISTS images_ad AFTER DELETE ON images BEGIN
            INSERT INTO images_fts(images_fts, rowid, filename, description, tags)
            VALUES ('delete', old.id, old.filename, old.description, old.tags);
        END;
        CREATE TRIGGER IF NOT EXISTS images_au AFTER UPDATE ON images BEGIN
            INSERT INTO images_fts(images_fts, rowid, filename, description, tags)
            VALUES ('delete', old.id, old.filename, old.description, old.tags);
            INSERT INTO images_fts(rowid, filename, description, tags)
            VALUES (new.id, new.filename, new.description, new.tags);
        END;
        ",
    )?;
    Ok(())
}
