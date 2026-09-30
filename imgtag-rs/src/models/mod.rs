//! Data models (serde structs) for DB rows and API request/response.

pub mod request;

use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Image {
    pub id: i64,
    pub path: String,
    pub filename: String,
    #[serde(default)]
    pub description: String,
    #[serde(default)]
    pub tags: String,
    #[serde(default = "default_status")]
    pub status: String,
    #[serde(default)]
    pub width: i64,
    #[serde(default)]
    pub height: i64,
    #[serde(default)]
    pub file_size: i64,
    #[serde(default)]
    pub date_taken: String,
    #[serde(default)]
    pub faces_scanned: i64,
    #[serde(default)]
    pub created_at: String,
}

fn default_status() -> String {
    "pending".to_string()
}

impl Image {
    /// Split the comma-separated tags string into a list.
    pub fn tag_list(&self) -> Vec<String> {
        crate::utils::split_tags(&self.tags)
    }

    /// Convert to a serde_json::Value with tag_list included (for /api/images response).
    pub fn to_dict_with_tags(&self) -> serde_json::Value {
        let mut v = serde_json::to_value(self).unwrap_or_default();
        if let Some(obj) = v.as_object_mut() {
            obj.insert("tag_list".to_string(), serde_json::Value::Array(
                self.tag_list().into_iter().map(serde_json::Value::String).collect()
            ));
        }
        v
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ImageDetail {
    #[serde(flatten)]
    pub image: Image,
    pub prev_id: Option<i64>,
    pub next_id: Option<i64>,
    pub tag_list: Vec<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Tag {
    pub id: i64,
    pub name: String,
    #[serde(default)]
    pub created_at: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct TagWithCount {
    pub id: i64,
    pub name: String,
    pub count: i64,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Folder {
    pub id: i64,
    pub path: String,
    #[serde(default)]
    pub alias: String,
    #[serde(default)]
    pub created_at: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct FolderWithCount {
    pub id: i64,
    pub path: String,
    #[serde(default)]
    pub alias: String,
    #[serde(default)]
    pub created_at: String,
    pub image_count: i64,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Person {
    pub id: i64,
    pub name: String,
    pub avatar_face_id: Option<i64>,
    #[serde(default)]
    pub ignored: i64,
    #[serde(default)]
    pub created_at: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct PersonWithStats {
    pub id: i64,
    pub name: String,
    pub avatar_face_id: Option<i64>,
    pub ignored: i64,
    pub image_count: i64,
    pub face_count: i64,
    pub created_at: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ImageFace {
    pub id: i64,
    pub image_id: i64,
    pub person_id: Option<i64>,
    pub bbox: String,
    pub score: f64,
    #[serde(default)]
    pub created_at: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ImageFaceWithPerson {
    pub id: i64,
    pub bbox: String,
    pub score: f64,
    pub person_id: Option<i64>,
    pub person_name: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Stats {
    pub total: i64,
    pub done: i64,
    pub pending: i64,
    pub failed: i64,
    pub tag_count: i64,
    pub total_size: i64,
}
