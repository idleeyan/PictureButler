//! Stats endpoint: image counts by status, tag count, total size.

use std::sync::Arc;

use axum::extract::{Query, State};
use axum::Json;
use serde::Deserialize;
use serde_json::json;

use super::AppState;
use crate::db::images as db;
use crate::error::AppError;

#[derive(Deserialize)]
pub struct StatsParams {
    #[serde(default)]
    pub folder: String,
}

/// GET /api/stats
pub async fn stats(
    State(state): State<Arc<AppState>>,
    Query(params): Query<StatsParams>,
) -> Result<Json<serde_json::Value>, AppError> {
    let folder_like = crate::config::resolve_folder_path(&params.folder);
    let s = tokio::task::spawn_blocking({
        let pool = state.db.clone();
        move || db::stats(&pool, folder_like.as_deref())
    })
    .await
    .map_err(|e| AppError::internal(e.to_string()))??;
    Ok(Json(json!(s)))
}
