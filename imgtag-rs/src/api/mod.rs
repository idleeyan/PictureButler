//! API module: router assembly and application state.

mod faces;
mod folders;
mod images;
mod persons;
mod process;
mod settings;
mod static_files;
mod stats;
mod system;
mod tags;

use std::sync::Arc;

use axum::routing::{delete, get, patch, post, Router};
use tower_http::cors::CorsLayer;
use tower_http::services::ServeDir;

use crate::config::AppConfig;
use crate::db::DbPool;
use crate::face::FaceApp;

/// Shared application state accessible to all route handlers.
pub struct AppState {
    pub config: AppConfig,
    pub db: DbPool,
    pub face: Arc<FaceApp>,
}

/// Build the axum router with all routes.
pub fn build_router(state: Arc<AppState>) -> Router {
    let static_dir = state.config.static_dir.clone();

    Router::new()
        // Static / index
        .route("/", get(static_files::index))
        .nest_service("/static", ServeDir::new(static_dir))
        // System
        .route("/api/health", get(health_check))
        .route("/api/system", get(system::system_info))
        .route("/api/stats", get(stats::stats))
        // Images
        .route("/api/images", get(images::list_images))
        .route("/api/image/:image_id", get(images::get_image).patch(images::update_image).delete(images::delete_image))
        .route("/api/image-file", get(images::serve_image_file))
        .route("/api/thumbnail", get(images::serve_thumbnail))
        // Process (scan / process / search / rescan / batch)
        .route("/api/scan", post(process::scan_folder))
        .route("/api/process", post(process::process_images))
        .route("/api/search", post(process::search_images))
        .route("/api/rescan", post(process::rescan_folder))
        .route("/api/images/batch", post(process::batch_delete))
        .route("/api/images/batch-tag", post(process::batch_tag))
        .route("/api/images/batch-move", post(process::batch_move))
        // Folders
        .route("/api/folders", get(folders::list_folders).post(folders::add_folder))
        .route("/api/folders/:folder_id", patch(folders::update_folder).delete(folders::delete_folder))
        // Tags
        .route("/api/tags", get(tags::list_tags))
        .route("/api/tags/search", get(tags::search_tags))
        .route("/api/tags/merge", post(tags::merge_tags))
        .route("/api/tags/:tag_id", delete(tags::delete_tag))
        // Persons
        .route("/api/persons", get(persons::list_persons))
        .route("/api/persons/batch", post(persons::batch_persons))
        .route("/api/persons/merge", post(persons::merge_persons))
        .route("/api/persons/:person_id", get(persons::get_person).patch(persons::update_person).delete(persons::delete_person))
        .route("/api/persons/:person_id/images", get(persons::get_person_images))
        .route("/api/persons/:person_id/suggestions", get(persons::get_suggestions))
        .route("/api/persons/:person_id/faces", get(faces::list_person_faces))
        // Faces
        .route("/api/face-thumbnail/:face_id", get(faces::face_thumbnail))
        .route("/api/faces/scan", post(faces::scan_faces))
        .route("/api/faces/recluster", post(faces::recluster_faces))
        .route("/api/images/:image_id/faces", get(faces::get_image_faces))
        .route("/api/faces/:face_id/person", patch(faces::reassign_face))
        // Settings
        .route("/api/settings", get(settings::get_settings).put(settings::update_settings))
        // CORS (allow all for local dev)
        .layer(CorsLayer::permissive())
        .with_state(state)
}

/// Simple health check endpoint.
async fn health_check() -> &'static str {
    "OK"
}
