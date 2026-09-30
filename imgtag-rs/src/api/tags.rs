//! Tags endpoints: list / search / delete / merge.

use std::sync::Arc;

use axum::extract::{Path, Query, State};
use axum::Json;
use serde::Deserialize;
use serde_json::json;

use super::AppState;
use crate::db::tags as db;
use crate::error::AppError;
use crate::models::request::MergeTagsRequest;

#[derive(Deserialize)]
pub struct ListTagsParams {
    #[serde(default)]
    pub folder: String,
    #[serde(default)]
    pub min_count: Option<i64>,
    #[serde(default)]
    pub limit: Option<i64>,
}

/// GET /api/tags
pub async fn list_tags(
    State(state): State<Arc<AppState>>,
    Query(params): Query<ListTagsParams>,
) -> Result<Json<serde_json::Value>, AppError> {
    let folder_like = crate::config::resolve_folder_path(&params.folder);
    let min_count = params.min_count.unwrap_or(1);
    let limit = params.limit.unwrap_or(500);
    let tags = tokio::task::spawn_blocking({
        let pool = state.db.clone();
        move || db::list_tags(&pool, folder_like.as_deref(), min_count, limit)
    })
    .await
    .map_err(|e| AppError::internal(e.to_string()))??;
    Ok(Json(json!(tags)))
}

#[derive(Deserialize)]
pub struct SearchTagsParams {
    #[serde(default)]
    pub q: String,
    #[serde(default)]
    pub limit: Option<i64>,
}

/// GET /api/tags/search
pub async fn search_tags(
    State(state): State<Arc<AppState>>,
    Query(params): Query<SearchTagsParams>,
) -> Result<Json<serde_json::Value>, AppError> {
    let q = params.q.trim().to_string();
    if q.is_empty() {
        return Ok(Json(json!([])));
    }
    let limit = params.limit.unwrap_or(20);
    let tags = tokio::task::spawn_blocking({
        let pool = state.db.clone();
        move || db::search_tags(&pool, &q, limit)
    })
    .await
    .map_err(|e| AppError::internal(e.to_string()))??;
    Ok(Json(json!(tags)))
}

/// DELETE /api/tags/{id}
pub async fn delete_tag(
    State(state): State<Arc<AppState>>,
    Path(tag_id): Path<i64>,
) -> Result<Json<serde_json::Value>, AppError> {
    let name = tokio::task::spawn_blocking({
        let pool = state.db.clone();
        move || db::delete_tag(&pool, tag_id)
    })
    .await
    .map_err(|e| AppError::internal(e.to_string()))??;
    Ok(Json(json!({"ok": true, "name": name})))
}

/// POST /api/tags/merge
pub async fn merge_tags(
    State(state): State<Arc<AppState>>,
    Json(body): Json<MergeTagsRequest>,
) -> Result<Json<serde_json::Value>, AppError> {
    let target = body.target.trim().to_string();
    if target.is_empty() {
        return Err(AppError::bad_request("target cannot be empty"));
    }
    if body.sources.is_empty() {
        return Err(AppError::bad_request("sources cannot be empty"));
    }
    tokio::task::spawn_blocking({
        let pool = state.db.clone();
        let sources = body.sources.clone();
        move || db::merge_tags(&pool, &sources, &target)
    })
    .await
    .map_err(|e| AppError::internal(e.to_string()))??;
    Ok(Json(json!({"ok": true})))
}
