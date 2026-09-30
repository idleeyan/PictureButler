//! Application configuration and settings schema.
//!
//! Mirrors the Python SETTINGS_SCHEMA in main.py, but with typed Rust structs.

use std::path::{Path, PathBuf};
use std::sync::Arc;

use parking_lot::RwLock;
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone)]
pub struct AppConfig {
    /// Project base directory (where db.sqlite, models/, static/ live)
    pub base_dir: PathBuf,
    pub db_path: PathBuf,
    pub faces_dir: PathBuf,
    pub thumbs_dir: PathBuf,
    pub cache_dir: PathBuf,
    pub models_dir: PathBuf,
    pub static_dir: PathBuf,

    /// Runtime-mutable settings (loaded from DB)
    pub settings: Arc<RwLock<Settings>>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Settings {
    pub face_det_thresh: f64,
    pub face_sim_thresh: f64,
    pub face_min_size: i64,
    pub face_enabled: bool,
    pub auto_suggest_merge: bool,
    pub lm_studio_url: String,
    pub lm_studio_model: String,
    pub max_image_size: i64,
}

impl Default for Settings {
    fn default() -> Self {
        Self {
            face_det_thresh: 0.5,
            face_sim_thresh: 0.5,
            face_min_size: 60,
            face_enabled: true,
            auto_suggest_merge: false,
            lm_studio_url: "http://127.0.0.1:1234/v1".to_string(),
            lm_studio_model: "qwen3vl-8b-uncensored-hauhaucs-aggressive".to_string(),
            max_image_size: 1024,
        }
    }
}

impl AppConfig {
    pub fn new(base_dir: PathBuf) -> Self {
        let db_path = base_dir.join("db.sqlite");
        let faces_dir = base_dir.join(".faces");
        let thumbs_dir = base_dir.join(".thumbs");
        let cache_dir = base_dir.join(".cache");
        let models_dir = base_dir.join("models");
        let static_dir = base_dir.join("static");
        Self {
            base_dir,
            db_path,
            faces_dir,
            thumbs_dir,
            cache_dir,
            models_dir,
            static_dir,
            settings: Arc::new(RwLock::new(Settings::default())),
        }
    }

    pub fn max_image_size_tuple(&self) -> (u32, u32) {
        let s = self.settings.read();
        let sz = s.max_image_size.max(64) as u32;
        (sz, sz)
    }

    pub fn settings_json(&self) -> serde_json::Value {
        let s = self.settings.read();
        serde_json::json!(s.clone())
    }

    /// Build the settings schema (for /api/settings response)
    pub fn settings_schema(&self) -> serde_json::Value {
        let s = self.settings.read();
        serde_json::json!({
            "face_det_thresh": {
                "value": s.face_det_thresh, "type": "float", "min": 0.1, "max": 0.9, "step": 0.05,
                "label": "Face detection threshold",
                "desc": "Faces with insightface score below this are discarded. Higher = stricter (0.1-0.9)"
            },
            "face_sim_thresh": {
                "value": s.face_sim_thresh, "type": "float", "min": 0.1, "max": 0.9, "step": 0.05,
                "label": "Same-person threshold",
                "desc": "Cosine similarity >= this means same person. Higher = more granular (0.1-0.9)"
            },
            "face_min_size": {
                "value": s.face_min_size, "type": "int", "min": 20, "max": 300, "step": 10,
                "label": "Min face size (px)",
                "desc": "Faces with width or height below this are skipped"
            },
            "face_enabled": {
                "value": s.face_enabled, "type": "bool",
                "label": "Enable face recognition",
                "desc": "Skip all face detection/clustering when off"
            },
            "auto_suggest_merge": {
                "value": s.auto_suggest_merge, "type": "bool",
                "label": "Auto show merge suggestions",
                "desc": "Analyze persons with <= 5 photos and suggest possible same-person merges"
            },
            "lm_studio_url": {
                "value": s.lm_studio_url, "type": "str",
                "label": "LM Studio API URL",
                "desc": "OpenAI-compatible API root URL"
            },
            "lm_studio_model": {
                "value": s.lm_studio_model, "type": "str",
                "label": "Vision model name",
                "desc": "Vision model ID loaded in LM Studio"
            },
            "max_image_size": {
                "value": s.max_image_size, "type": "int", "min": 512, "max": 2048, "step": 128,
                "label": "Max image side for model",
                "desc": "Resize upper bound before sending to VLM. Larger = more accurate but slower"
            },
        })
    }

    pub fn models_buffalo_l_dir(&self) -> PathBuf {
        self.models_dir.join("buffalo_l")
    }
}

/// Logging helpers matching Python output format `[ImgTag] ...`
pub fn info(msg: &str) {
    tracing::info!("[ImgTag] {}", msg);
}

pub fn warn(msg: &str) {
    tracing::warn!("[ImgTag] ⚠ {}", msg);
}

/// Helper to normalize a folder path for SQL LIKE queries (mirrors _resolve_folder_path)
pub fn resolve_folder_path(folder: &str) -> Option<String> {
    if folder.is_empty() || folder == "__all" {
        return None;
    }
    let p = Path::new(folder);
    match p.canonicalize() {
        Ok(canon) => Some(canon.to_string_lossy().into_owned()),
        Err(_) => Some(p.to_string_lossy().into_owned()),
    }
}
