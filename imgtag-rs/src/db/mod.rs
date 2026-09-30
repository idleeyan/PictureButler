//! Database module: connection pool, schema initialization, migrations.
//!
//! Mirrors the Python init_db() in main.py.

pub mod images;
pub mod migrate;
pub mod persons;
pub mod schema;
pub mod tags;
pub mod folders;
pub mod settings;

use r2d2::Pool;
use r2d2_sqlite::SqliteConnectionManager;
use rusqlite::Connection;

use crate::config::AppConfig;
use crate::error::AppResult;

pub type DbPool = Pool<SqliteConnectionManager>;
pub type DbConn = r2d2::PooledConnection<SqliteConnectionManager>;

/// Initialize the connection pool with WAL mode and foreign keys enabled.
pub fn init_pool(db_path: &std::path::Path) -> AppResult<DbPool> {
    let manager = SqliteConnectionManager::file(db_path).with_init(|c| {
        // PRAGMA journal_mode=WAL returns a row; consume it via query_row
        let _: String = c.query_row("PRAGMA journal_mode=WAL;", [], |r| r.get(0))?;
        c.execute("PRAGMA foreign_keys=ON;", [])?;
        Ok(())
    });
    let pool = Pool::builder().max_size(8).build(manager)?;
    Ok(pool)
}

/// Get a connection from the pool.
pub fn get_conn(pool: &DbPool) -> AppResult<DbConn> {
    pool.get()
        .map_err(|e| crate::error::AppError::Pool(e.to_string()))
}

/// Initialize the database: create tables, run migrations, create triggers.
/// Note: backfill operations are skipped here to avoid blocking startup;
/// they can be triggered explicitly via API if needed.
pub fn init_db(pool: &DbPool) -> AppResult<()> {
    let conn = get_conn(pool)?;
    schema::create_tables(&conn)?;
    migrate::migrate_all(&conn)?;
    schema::create_indexes_and_triggers(&conn)?;
    tags::backfill_image_tags(&conn)?;
    Ok(())
}

/// Spawn a background task to backfill missing image metadata.
/// This avoids blocking server startup when many images lack width/height/file_size/date_taken.
pub fn spawn_backfill_task(pool: DbPool) {
    tokio::task::spawn_blocking(move || {
        crate::config::info("Background: backfill_image_meta starting...");
        match get_conn(&pool) {
            Ok(conn) => {
                if let Err(e) = backfill_image_meta(&conn) {
                    crate::config::warn(&format!("Background backfill_image_meta failed: {}", e));
                } else {
                    crate::config::info("Background: backfill_image_meta done.");
                }
            }
            Err(e) => {
                crate::config::warn(&format!("Background backfill: cannot get conn: {}", e));
            }
        }
    });
}

/// Load settings from DB into the AppConfig runtime state.
pub fn load_settings_into_config(pool: &DbPool, config: &AppConfig) -> AppResult<()> {
    let values = settings::load_settings(pool)?;
    let mut s = config.settings.write();
    *s = values;
    Ok(())
}

/// Backfill missing image metadata (width/height/file_size/date_taken).
/// Mirrors _backfill_image_meta in main.py.
fn backfill_image_meta(conn: &Connection) -> AppResult<()> {
    use rusqlite::params;
    let mut stmt = conn.prepare(
        "SELECT id, path FROM images WHERE (width IS NULL OR width=0
         OR file_size IS NULL OR file_size=0
         OR date_taken IS NULL OR date_taken='')",
    )?;
    let rows: Vec<(i64, String)> = stmt
        .query_map([], |r| Ok((r.get::<_, i64>(0)?, r.get::<_, String>(1)?)))?
        .filter_map(|r| r.ok())
        .collect();
    drop(stmt);

    for (id, path_str) in rows {
        let path = std::path::Path::new(&path_str);
        if !path.exists() {
            continue;
        }
        let Ok(meta) = std::fs::metadata(path) else { continue };
        let size = meta.len() as i64;
        let (w, h) = match crate::imageproc::io::read_dimensions(path) {
            Ok(d) => d,
            Err(_) => (0, 0),
        };
        let dt = crate::imageproc::exif::extract_date_taken(path).unwrap_or_default();
        conn.execute(
            "UPDATE images SET width=?, height=?, file_size=?, date_taken=? WHERE id=?",
            params![w, h, size, dt, id],
        )?;
    }
    Ok(())
}
