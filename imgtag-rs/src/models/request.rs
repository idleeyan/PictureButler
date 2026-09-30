//! API request models (mirrors Pydantic models in main.py).

use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Deserialize)]
pub struct FolderRequest {
    pub folder: String,
}

#[derive(Debug, Clone, Deserialize)]
pub struct ProcessRequest {
    #[serde(default)]
    pub folder: String,
    #[serde(default)]
    pub image_ids: String,
    #[serde(default)]
    pub limit: i64,
    /// true = 不筛状态，全部重新处理
    #[serde(default)]
    pub force: bool,
}

#[derive(Debug, Clone, Deserialize)]
pub struct SearchRequest {
    #[serde(default)]
    pub query: String,
    #[serde(default)]
    pub folder: String,
    #[serde(default)]
    pub tags: Vec<String>,
    #[serde(default = "default_tag_mode")]
    pub tag_mode: String,
    #[serde(default = "default_page")]
    pub page: i64,
    #[serde(default = "default_per_page")]
    pub per_page: i64,
}

fn default_tag_mode() -> String { "and".to_string() }
fn default_page() -> i64 { 1 }
fn default_per_page() -> i64 { 30 }

#[derive(Debug, Clone, Deserialize)]
pub struct AddFolderRequest {
    pub path: String,
    #[serde(default)]
    pub alias: String,
}

#[derive(Debug, Clone, Deserialize)]
pub struct UpdateFolderRequest {
    pub alias: Option<String>,
}

#[derive(Debug, Clone, Deserialize)]
pub struct UpdateImageRequest {
    pub description: Option<String>,
    pub tags: Option<String>,
}

#[derive(Debug, Clone, Deserialize)]
pub struct UpdateSettingsRequest {
    pub face_det_thresh: Option<f64>,
    pub face_sim_thresh: Option<f64>,
    pub face_min_size: Option<i64>,
    pub face_enabled: Option<bool>,
    pub auto_suggest_merge: Option<bool>,
    pub lm_studio_url: Option<String>,
    pub lm_studio_model: Option<String>,
    pub max_image_size: Option<i64>,
}

// Batch operations (body: dict = Body(...) in Python)
#[derive(Debug, Clone, Deserialize)]
pub struct BatchDeleteRequest {
    pub ids: Vec<i64>,
    #[serde(default)]
    pub delete_file: bool,
}

#[derive(Debug, Clone, Deserialize)]
pub struct BatchTagRequest {
    pub ids: Vec<i64>,
    pub tag: String,
    #[serde(default = "default_add")]
    pub mode: String,
}

fn default_add() -> String { "add".to_string() }

#[derive(Debug, Clone, Deserialize)]
pub struct BatchMoveRequest {
    pub ids: Vec<i64>,
    pub target_dir: String,
}

#[derive(Debug, Clone, Deserialize)]
pub struct BatchPersonsRequest {
    pub ids: Vec<i64>,
    pub action: String,
    #[serde(default)]
    pub delete_faces: bool,
}

#[derive(Debug, Clone, Deserialize)]
pub struct MergeTagsRequest {
    pub sources: Vec<String>,
    pub target: String,
}

#[derive(Debug, Clone, Deserialize)]
pub struct MergePersonsRequest {
    pub source_ids: Vec<i64>,
    pub target_id: i64,
}

#[derive(Debug, Clone, Deserialize)]
pub struct ReassignFaceRequest {
    pub person_id: Option<i64>,
    pub new_name: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
pub struct ImageListResponse {
    pub total: i64,
    pub page: i64,
    pub per_page: i64,
    pub images: Vec<serde_json::Value>,
}
