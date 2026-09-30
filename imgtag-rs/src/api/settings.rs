//! Settings endpoints: get (values + schema) / put (update).

use std::sync::Arc;

use axum::extract::State;
use axum::Json;
use serde_json::json;

use super::AppState;
use crate::db::settings as db;
use crate::error::AppError;
use crate::models::request::UpdateSettingsRequest;

/// GET /api/settings — return current values + schema metadata for UI rendering.
pub async fn get_settings(State(state): State<Arc<AppState>>) -> Json<serde_json::Value> {
    let values = state.config.settings_json();
    let schema = state.config.settings_schema();
    Json(json!({
        "values": values,
        "schema": schema,
    }))
}

/// PUT /api/settings — update settings, persist to DB, apply to runtime.
pub async fn update_settings(
    State(state): State<Arc<AppState>>,
    Json(body): Json<UpdateSettingsRequest>,
) -> Result<Json<serde_json::Value>, AppError> {
    // Load current settings, apply partial updates
    let mut current = state.config.settings.read().clone();

    if let Some(v) = body.face_det_thresh {
        current.face_det_thresh = v.clamp(0.1, 0.9);
    }
    if let Some(v) = body.face_sim_thresh {
        current.face_sim_thresh = v.clamp(0.1, 0.9);
    }
    if let Some(v) = body.face_min_size {
        current.face_min_size = v.clamp(20, 300);
    }
    if let Some(v) = body.face_enabled {
        current.face_enabled = v;
    }
    if let Some(v) = body.auto_suggest_merge {
        current.auto_suggest_merge = v;
    }
    if let Some(v) = body.lm_studio_url {
        current.lm_studio_url = v.trim().to_string();
    }
    if let Some(v) = body.lm_studio_model {
        current.lm_studio_model = v.trim().to_string();
    }
    if let Some(v) = body.max_image_size {
        current.max_image_size = v.clamp(512, 2048);
    }

    // Persist + reload
    let reloaded = tokio::task::spawn_blocking({
        let pool = state.db.clone();
        let s = current.clone();
        move || db::update_settings(&pool, &s)
    })
    .await
    .map_err(|e| AppError::internal(e.to_string()))??;

    // Apply to runtime
    {
        let mut s = state.config.settings.write();
        *s = reloaded.clone();
    }

    // Force face app to reload models with new thresholds
    state.face.reset();

    Ok(Json(json!({"ok": true, "values": reloaded})))
}
