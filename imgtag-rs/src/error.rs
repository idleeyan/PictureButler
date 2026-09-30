//! Error types for the application.
//!
//! Maps Python's HTTPException to typed Rust errors with HTTP status codes.

use axum::http::StatusCode;
use axum::response::{IntoResponse, Response};
use axum::Json;
use serde_json::json;
use thiserror::Error;

pub type AppResult<T> = Result<T, AppError>;

#[derive(Error, Debug)]
pub enum AppError {
    #[error("{message}")]
    NotFound { message: String },

    #[error("{message}")]
    BadRequest { message: String },

    #[error("{message}")]
    Internal { message: String },

    #[error("database error: {0}")]
    Database(#[from] rusqlite::Error),

    #[error("pool error: {0}")]
    Pool(String),

    #[error("io error: {0}")]
    Io(#[from] std::io::Error),

    #[error("image error: {0}")]
    Image(#[from] image::ImageError),

    #[error("http error: {0}")]
    Http(#[from] reqwest::Error),

    #[error("json error: {0}")]
    Json(#[from] serde_json::Error),

    #[error("onnx inference error: {0}")]
    Ort(String),

    #[error("pool error: {0}")]
    R2d2(#[from] r2d2::Error),

    #[error("{message}")]
    Other { message: String },
}

/// Generic From implementation for ort::Error<T> (ort 2.0.0-rc.13 uses generic errors).
impl<T> From<ort::Error<T>> for AppError {
    fn from(e: ort::Error<T>) -> Self {
        Self::Ort(e.to_string())
    }
}

impl AppError {
    pub fn not_found(msg: impl Into<String>) -> Self {
        Self::NotFound { message: msg.into() }
    }

    pub fn bad_request(msg: impl Into<String>) -> Self {
        Self::BadRequest { message: msg.into() }
    }

    pub fn internal(msg: impl Into<String>) -> Self {
        Self::Internal { message: msg.into() }
    }

    pub fn other(msg: impl Into<String>) -> Self {
        Self::Other { message: msg.into() }
    }

    pub fn status_code(&self) -> StatusCode {
        match self {
            Self::NotFound { .. } => StatusCode::NOT_FOUND,
            Self::BadRequest { .. } => StatusCode::BAD_REQUEST,
            _ => StatusCode::INTERNAL_SERVER_ERROR,
        }
    }
}

impl IntoResponse for AppError {
    fn into_response(self) -> Response {
        let status = self.status_code();
        let body = Json(json!({
            "detail": self.to_string(),
        }));
        (status, body).into_response()
    }
}

impl From<anyhow::Error> for AppError {
    fn from(e: anyhow::Error) -> Self {
        Self::Internal { message: e.to_string() }
    }
}
