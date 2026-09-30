//! Folders endpoints: list / add / update alias / delete.

use std::sync::Arc;

use axum::extract::{Path, State};
use axum::Json;
use serde_json::json;

use super::AppState;
use crate::db::folders as db;
use crate::error::AppError;
use crate::models::request::{AddFolderRequest, UpdateFolderRequest};

/// GET /api/folders
pub async fn list_folders(State(state): State<Arc<AppState>>) -> Result<Json<serde_json::Value>, AppError> {
    let folders = tokio::task::spawn_blocking({
        let pool = state.db.clone();
        move || db::list_folders(&pool)
    })
    .await
    .map_err(|e| AppError::internal(e.to_string()))??;
    Ok(Json(json!(folders)))
}

/// POST /api/folders
pub async fn add_folder(
    State(state): State<Arc<AppState>>,
    Json(body): Json<AddFolderRequest>,
) -> Result<Json<serde_json::Value>, AppError> {
    let path = body.path.trim().to_string();
    if path.is_empty() {
        return Err(AppError::bad_request("path cannot be empty"));
    }
    let folder = tokio::task::spawn_blocking({
        let pool = state.db.clone();
        let alias = body.alias.clone();
        move || db::add_folder(&pool, &path, &alias)
    })
    .await
    .map_err(|e| AppError::internal(e.to_string()))??;
    Ok(Json(json!(folder)))
}

/// PATCH /api/folders/{id}
pub async fn update_folder(
    State(state): State<Arc<AppState>>,
    Path(folder_id): Path<i64>,
    Json(body): Json<UpdateFolderRequest>,
) -> Result<Json<serde_json::Value>, AppError> {
    if let Some(alias) = body.alias {
        tokio::task::spawn_blocking({
            let pool = state.db.clone();
            move || db::update_folder_alias(&pool, folder_id, &alias)
        })
        .await
        .map_err(|e| AppError::internal(e.to_string()))??;
    }
    Ok(Json(json!({"ok": true})))
}

/// DELETE /api/folders/{id}
pub async fn delete_folder(
    State(state): State<Arc<AppState>>,
    Path(folder_id): Path<i64>,
) -> Result<Json<serde_json::Value>, AppError> {
    tokio::task::spawn_blocking({
        let pool = state.db.clone();
        move || db::delete_folder(&pool, folder_id)
    })
    .await
    .map_err(|e| AppError::internal(e.to_string()))??;
    Ok(Json(json!({"ok": true})))
}
