//! Image endpoints: list / get / update / file serve / thumbnail / delete.

use std::path::PathBuf;
use std::sync::Arc;

use axum::body::Body;
use axum::extract::{Path, Query, State};
use axum::http::{header, StatusCode};
use axum::response::{IntoResponse, Response};
use axum::Json;
use serde::Deserialize;
use serde_json::json;

use super::AppState;
use crate::db::images as db;
use crate::error::AppError;
use crate::models::request::UpdateImageRequest;

#[derive(Deserialize)]
pub struct ListImagesParams {
    #[serde(default)]
    pub folder: String,
    #[serde(default)]
    pub status: String,
    #[serde(default)]
    pub tags: String,
    #[serde(default)]
    pub tag_mode: String,
    #[serde(default)]
    pub sort: String,
    #[serde(default = "default_page")]
    pub page: i64,
    #[serde(default = "default_per_page")]
    pub per_page: i64,
}

fn default_page() -> i64 { 1 }
fn default_per_page() -> i64 { 50 }

/// GET /api/images
pub async fn list_images(
    State(state): State<Arc<AppState>>,
    Query(params): Query<ListImagesParams>,
) -> Result<Json<serde_json::Value>, AppError> {
    let folder_like = crate::config::resolve_folder_path(&params.folder);
    let status = if params.status.is_empty() { None } else { Some(params.status.as_str()) };
    let tags: Vec<String> = crate::utils::split_tags(&params.tags);
    let tag_mode = if params.tag_mode.is_empty() { "and" } else { &params.tag_mode };
    let sort = if params.sort.is_empty() { "date_desc" } else { &params.sort };
    let per_page = params.per_page.clamp(1, 10000);
    let page = params.page.max(1);

    let resp = tokio::task::spawn_blocking({
        let pool = state.db.clone();
        let tags = tags.clone();
        let tag_mode = tag_mode.to_string();
        let sort = sort.to_string();
        let status = status.map(|s| s.to_string());
        move || db::list_images(
            &pool,
            folder_like.as_deref(),
            status.as_deref(),
            &tags,
            &tag_mode,
            &sort,
            page,
            per_page,
        )
    })
    .await
    .map_err(|e| AppError::internal(e.to_string()))??;

    Ok(Json(json!(resp)))
}

#[derive(Deserialize)]
pub struct GetImageParams {
    #[serde(default)]
    pub folder: String,
}

/// GET /api/image/{id}
pub async fn get_image(
    State(state): State<Arc<AppState>>,
    Path(image_id): Path<i64>,
    Query(params): Query<GetImageParams>,
) -> Result<Json<serde_json::Value>, AppError> {
    let folder_like = crate::config::resolve_folder_path(&params.folder);
    let detail = tokio::task::spawn_blocking({
        let pool = state.db.clone();
        move || db::get_image(&pool, image_id, folder_like.as_deref())
    })
    .await
    .map_err(|e| AppError::internal(e.to_string()))??;
    Ok(Json(json!(detail)))
}

/// PATCH /api/image/{id}
pub async fn update_image(
    State(state): State<Arc<AppState>>,
    Path(image_id): Path<i64>,
    Json(body): Json<UpdateImageRequest>,
) -> Result<Json<serde_json::Value>, AppError> {
    tokio::task::spawn_blocking({
        let pool = state.db.clone();
        let desc = body.description.clone();
        let tags = body.tags.clone();
        move || db::update_image(&pool, image_id, desc.as_deref(), tags.as_deref())
    })
    .await
    .map_err(|e| AppError::internal(e.to_string()))??;
    Ok(Json(json!({"ok": true})))
}

#[derive(Deserialize)]
pub struct ImageFileParams {
    pub path: String,
    #[serde(default)]
    pub thumb: i64,
}

/// GET /api/image-file
pub async fn serve_image_file(
    Query(params): Query<ImageFileParams>,
) -> Result<Response, AppError> {
    let p = PathBuf::from(&params.path);
    if !p.exists() {
        return Err(AppError::not_found("File not found"));
    }

    if params.thumb > 0 {
        // Generate thumbnail in-memory and return as JPEG
        let size = params.thumb as u32;
        let p_clone = p.clone();
        let result = tokio::task::spawn_blocking(move || -> Result<Vec<u8>, AppError> {
            let img = crate::imageproc::io::decode_rgb(&p_clone)?;
            let thumb = crate::imageproc::resize::thumbnail(&img, size);
            Ok(crate::imageproc::io::encode_jpeg(&thumb, 80))
        })
        .await
        .map_err(|e| AppError::internal(e.to_string()))?;
        match result {
            Ok(bytes) => {
                return Ok((
                    [(header::CONTENT_TYPE, "image/jpeg")],
                    Body::from(bytes),
                ).into_response());
            }
            Err(_) => {
                // Fallback: serve original file
            }
        }
    }

    // Serve original file (guess content type from extension)
    let mime = mime_guess::from_path(&p).first_or_octet_stream();
    let bytes = tokio::fs::read(&p)
        .await
        .map_err(|e| AppError::internal(format!("Failed to read file: {}", e)))?;
    Ok((
        StatusCode::OK,
        [(header::CONTENT_TYPE, mime.as_ref())],
        Body::from(bytes),
    ).into_response())
}

#[derive(Deserialize)]
pub struct ThumbnailParams {
    pub path: String,
    #[serde(default = "default_thumb_size")]
    pub size: u32,
}

fn default_thumb_size() -> u32 { 400 }

/// GET /api/thumbnail — returns cached thumbnail JPEG.
pub async fn serve_thumbnail(
    State(state): State<Arc<AppState>>,
    Query(params): Query<ThumbnailParams>,
) -> Result<Response, AppError> {
    let p = PathBuf::from(&params.path);
    if !p.exists() {
        return Err(AppError::not_found("File not found"));
    }

    let cache_dir = state.config.thumbs_dir.clone();
    let size = params.size.clamp(64, 1024);

    let cache_file = tokio::task::spawn_blocking({
        let p = p.clone();
        move || crate::imageproc::thumb_cache::get_or_create(&p, size, &cache_dir)
    })
    .await
    .map_err(|e| AppError::internal(e.to_string()))?;

    match cache_file {
        Ok(path) => {
            let bytes = tokio::fs::read(&path)
                .await
                .map_err(|e| AppError::internal(format!("Failed to read thumbnail: {}", e)))?;
            Ok((
                StatusCode::OK,
                [(header::CONTENT_TYPE, "image/jpeg")],
                Body::from(bytes),
            ).into_response())
        }
        Err(_) => {
            // Fallback: serve original
            let mime = mime_guess::from_path(&p).first_or_octet_stream();
            let bytes = tokio::fs::read(&p)
                .await
                .map_err(|e| AppError::internal(format!("Failed to read file: {}", e)))?;
            Ok((
                StatusCode::OK,
                [(header::CONTENT_TYPE, mime.as_ref())],
                Body::from(bytes),
            ).into_response())
        }
    }
}

/// DELETE /api/image/{id}
pub async fn delete_image(
    State(state): State<Arc<AppState>>,
    Path(image_id): Path<i64>,
) -> Result<Json<serde_json::Value>, AppError> {
    let path = tokio::task::spawn_blocking({
        let pool = state.db.clone();
        move || db::delete_image(&pool, image_id)
    })
    .await
    .map_err(|e| AppError::internal(e.to_string()))??;
    // Clean thumbnail cache for this path
    crate::imageproc::thumb_cache::clean_for_path(&path, &state.config.thumbs_dir);
    Ok(Json(json!({"ok": true, "path": path})))
}
