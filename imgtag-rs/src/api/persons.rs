//! Person endpoints: list / get / images / suggestions / update / merge / delete / batch.

use std::sync::Arc;

use axum::extract::{Path, Query, State};
use axum::Json;
use serde::Deserialize;
use serde_json::json;

use super::AppState;
use crate::db::persons as db;
use crate::error::AppError;
use crate::models::request::{BatchPersonsRequest, MergePersonsRequest};

#[derive(Deserialize)]
pub struct ListPersonsParams {
    #[serde(default)]
    pub folder: String,
    #[serde(default)]
    pub include_ignored: bool,
}

/// GET /api/persons
pub async fn list_persons(
    State(state): State<Arc<AppState>>,
    Query(params): Query<ListPersonsParams>,
) -> Result<Json<serde_json::Value>, AppError> {
    let folder_like = crate::config::resolve_folder_path(&params.folder);
    let persons = tokio::task::spawn_blocking({
        let pool = state.db.clone();
        move || db::list_persons(&pool, folder_like.as_deref(), params.include_ignored)
    })
    .await
    .map_err(|e| AppError::internal(e.to_string()))??;
    Ok(Json(json!(persons)))
}

/// GET /api/persons/{id}
pub async fn get_person(
    State(state): State<Arc<AppState>>,
    Path(person_id): Path<i64>,
) -> Result<Json<serde_json::Value>, AppError> {
    let p = tokio::task::spawn_blocking({
        let pool = state.db.clone();
        move || db::get_person(&pool, person_id)
    })
    .await
    .map_err(|e| AppError::internal(e.to_string()))??;
    Ok(Json(json!(p)))
}

#[derive(Deserialize)]
pub struct PersonImagesParams {
    #[serde(default = "default_page")]
    pub page: i64,
    #[serde(default = "default_per_page")]
    pub per_page: i64,
}

fn default_page() -> i64 { 1 }
fn default_per_page() -> i64 { 50 }

/// GET /api/persons/{id}/images
pub async fn get_person_images(
    State(state): State<Arc<AppState>>,
    Path(person_id): Path<i64>,
    Query(params): Query<PersonImagesParams>,
) -> Result<Json<serde_json::Value>, AppError> {
    let per_page = params.per_page.clamp(1, 10000);
    let page = params.page.max(1);
    let (total, images) = tokio::task::spawn_blocking({
        let pool = state.db.clone();
        move || db::get_person_images(&pool, person_id, page, per_page)
    })
    .await
    .map_err(|e| AppError::internal(e.to_string()))??;

    let images_json: Vec<serde_json::Value> = images.iter().map(|img| img.to_dict_with_tags()).collect();
    Ok(Json(json!({
        "total": total,
        "page": page,
        "per_page": per_page,
        "images": images_json,
    })))
}

/// GET /api/persons/{id}/suggestions
pub async fn get_suggestions(
    State(state): State<Arc<AppState>>,
    Path(person_id): Path<i64>,
) -> Result<Json<serde_json::Value>, AppError> {
    let suggestions = tokio::task::spawn_blocking({
        let pool = state.db.clone();
        move || db::get_suggestions(&pool, person_id, 10)
    })
    .await
    .map_err(|e| AppError::internal(e.to_string()))??;
    Ok(Json(suggestions))
}

#[derive(Deserialize)]
pub struct UpdatePersonRequest {
    pub name: Option<String>,
    pub avatar_face_id: Option<i64>,
    pub ignored: Option<bool>,
}

/// PATCH /api/persons/{id}
pub async fn update_person(
    State(state): State<Arc<AppState>>,
    Path(person_id): Path<i64>,
    Json(body): Json<UpdatePersonRequest>,
) -> Result<Json<serde_json::Value>, AppError> {
    let name = body.name.as_deref().map(|s| s.trim()).filter(|s| !s.is_empty());
    tokio::task::spawn_blocking({
        let pool = state.db.clone();
        let n = name.map(|s| s.to_string());
        move || db::update_person(&pool, person_id, n.as_deref(), body.avatar_face_id, body.ignored)
    })
    .await
    .map_err(|e| AppError::internal(e.to_string()))??;
    Ok(Json(json!({"ok": true})))
}

/// POST /api/persons/merge
pub async fn merge_persons(
    State(state): State<Arc<AppState>>,
    Json(body): Json<MergePersonsRequest>,
) -> Result<Json<serde_json::Value>, AppError> {
    if body.source_ids.is_empty() {
        return Err(AppError::bad_request("source_ids cannot be empty"));
    }
    tokio::task::spawn_blocking({
        let pool = state.db.clone();
        let source_ids = body.source_ids.clone();
        move || db::merge_persons(&pool, &source_ids, body.target_id)
    })
    .await
    .map_err(|e| AppError::internal(e.to_string()))??;
    Ok(Json(json!({"ok": true})))
}

#[derive(Deserialize)]
pub struct DeletePersonParams {
    #[serde(default)]
    pub delete_faces: bool,
}

/// DELETE /api/persons/{id}
pub async fn delete_person(
    State(state): State<Arc<AppState>>,
    Path(person_id): Path<i64>,
    Query(params): Query<DeletePersonParams>,
) -> Result<Json<serde_json::Value>, AppError> {
    tokio::task::spawn_blocking({
        let pool = state.db.clone();
        move || db::delete_person(&pool, person_id, params.delete_faces)
    })
    .await
    .map_err(|e| AppError::internal(e.to_string()))??;
    Ok(Json(json!({"ok": true})))
}

/// POST /api/persons/batch
pub async fn batch_persons(
    State(state): State<Arc<AppState>>,
    Json(body): Json<BatchPersonsRequest>,
) -> Result<Json<serde_json::Value>, AppError> {
    if body.ids.is_empty() {
        return Err(AppError::bad_request("ids cannot be empty"));
    }
    let (affected, failures) = tokio::task::spawn_blocking({
        let pool = state.db.clone();
        let ids = body.ids.clone();
        let action = body.action.clone();
        move || db::batch_persons(&pool, &ids, &action, body.delete_faces)
    })
    .await
    .map_err(|e| AppError::internal(e.to_string()))??;

    Ok(Json(json!({
        "ok": true,
        "total": body.ids.len(),
        "affected": affected,
        "action": body.action,
        "failures": failures,
    })))
}
