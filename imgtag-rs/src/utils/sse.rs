//! SSE (Server-Sent Events) helper for streaming progress.

/// SSE progress event payload.
#[derive(Debug, Clone, serde::Serialize)]
pub struct ProgressEvent {
    #[serde(rename = "type")]
    pub event_type: String,
    pub current: i64,
    pub total: i64,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub filename: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub status: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub description: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub tags: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub faces: Option<i64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub total_faces: Option<i64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub image_id: Option<i64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub error: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub new_persons: Option<i64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub clusters: Option<i64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub named_seeds: Option<i64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub scanned: Option<i64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub failed: Option<i64>,
}

impl ProgressEvent {
    pub fn start(total: i64) -> Self {
        Self {
            event_type: "start".into(),
            current: 0,
            total,
            filename: None,
            status: None,
            description: None,
            tags: None,
            faces: None,
            total_faces: None,
            image_id: None,
            error: None,
            new_persons: None,
            clusters: None,
            named_seeds: None,
            scanned: None,
            failed: None,
        }
    }

    pub fn progress(current: i64, total: i64) -> Self {
        Self {
            event_type: "progress".into(),
            current,
            total,
            filename: None,
            status: None,
            description: None,
            tags: None,
            faces: None,
            total_faces: None,
            image_id: None,
            error: None,
            new_persons: None,
            clusters: None,
            named_seeds: None,
            scanned: None,
            failed: None,
        }
    }

    pub fn done(total: i64) -> Self {
        Self {
            event_type: "done".into(),
            current: total,
            total,
            filename: None,
            status: None,
            description: None,
            tags: None,
            faces: None,
            total_faces: None,
            image_id: None,
            error: None,
            new_persons: None,
            clusters: None,
            named_seeds: None,
            scanned: None,
            failed: None,
        }
    }
}
