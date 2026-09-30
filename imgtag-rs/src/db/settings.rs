//! Settings KV store (mirrors _load_settings_from_db + update_settings).

use rusqlite::params;

use crate::config::Settings;
use crate::db::{get_conn, DbPool};
use crate::error::AppResult;

/// Load settings from DB, falling back to defaults.
pub fn load_settings(pool: &DbPool) -> AppResult<Settings> {
    let conn = get_conn(pool)?;
    let mut s = Settings::default();
    let mut stmt = conn.prepare("SELECT key, value FROM settings")?;
    let rows = stmt.query_map([], |r| {
        Ok((r.get::<_, String>(0)?, r.get::<_, String>(1)?))
    })?;
    for row in rows.flatten() {
        apply_setting(&mut s, &row.0, &row.1);
    }
    Ok(s)
}

/// Apply a single key-value pair to a Settings struct.
fn apply_setting(s: &mut Settings, key: &str, value: &str) {
    match key {
        "face_det_thresh" => s.face_det_thresh = value.parse().unwrap_or(s.face_det_thresh),
        "face_sim_thresh" => s.face_sim_thresh = value.parse().unwrap_or(s.face_sim_thresh),
        "face_min_size" => s.face_min_size = value.parse().unwrap_or(s.face_min_size),
        "face_enabled" => s.face_enabled = value == "1",
        "auto_suggest_merge" => s.auto_suggest_merge = value == "1",
        "lm_studio_url" => s.lm_studio_url = value.to_string(),
        "lm_studio_model" => s.lm_studio_model = value.to_string(),
        "max_image_size" => s.max_image_size = value.parse().unwrap_or(s.max_image_size),
        _ => {}
    }
}

/// Update settings in DB and apply to runtime config.
pub fn update_settings(pool: &DbPool, updates: &Settings) -> AppResult<Settings> {
    let conn = get_conn(pool)?;
    let pairs = [
        ("face_det_thresh", updates.face_det_thresh.to_string()),
        ("face_sim_thresh", updates.face_sim_thresh.to_string()),
        ("face_min_size", updates.face_min_size.to_string()),
        ("face_enabled", if updates.face_enabled { "1" } else { "0" }.to_string()),
        ("auto_suggest_merge", if updates.auto_suggest_merge { "1" } else { "0" }.to_string()),
        ("lm_studio_url", updates.lm_studio_url.clone()),
        ("lm_studio_model", updates.lm_studio_model.clone()),
        ("max_image_size", updates.max_image_size.to_string()),
    ];
    for (k, v) in &pairs {
        conn.execute(
            "INSERT INTO settings (key, value) VALUES (?, ?)
             ON CONFLICT(key) DO UPDATE SET value=excluded.value",
            params![k, v],
        )?;
    }
    // Reload and return
    drop(conn);
    load_settings(pool)
}
