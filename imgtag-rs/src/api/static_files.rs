//! Static file serving: index.html at /, plus static/ directory.

use std::sync::Arc;

use axum::body::Body;
use axum::extract::State;
use axum::http::{header, StatusCode};
use axum::response::{IntoResponse, Response};

use super::AppState;
use crate::error::AppError;

/// GET / — serve the SPA index.html from base_dir.
pub async fn index(State(state): State<Arc<AppState>>) -> Result<Response, AppError> {
    let index_path = state.config.base_dir.join("index.html");
    if !index_path.exists() {
        return Err(AppError::not_found("index.html not found"));
    }
    let bytes = tokio::fs::read(&index_path)
        .await
        .map_err(|e| AppError::internal(format!("Failed to read index.html: {}", e)))?;
    Ok((
        StatusCode::OK,
        [(header::CONTENT_TYPE, "text/html; charset=utf-8")],
        Body::from(bytes),
    ).into_response())
}
