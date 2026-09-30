//! System info endpoint: checks LM Studio connectivity.

use axum::extract::State;
use axum::Json;
use serde_json::json;

use super::AppState;

/// GET /api/system — check LM Studio connectivity and model status.
pub async fn system_info(State(state): State<std::sync::Arc<AppState>>) -> Json<serde_json::Value> {
    let settings = state.config.settings.read().clone();
    let lm_url = settings.lm_studio_url.clone();
    let model = settings.lm_studio_model.clone();

    let mut info = json!({
        "lm_studio_url": lm_url,
        "model": model,
        "online": false,
        "models": [],
    });

    match reqwest::Client::builder()
        .timeout(std::time::Duration::from_secs(5))
        .build()
    {
        Ok(client) => {
            match client
                .get(format!("{}/models", lm_url.trim_end_matches('/')))
                .send()
                .await
            {
                Ok(resp) if resp.status().is_success() => {
                    if let Ok(data) = resp.json::<serde_json::Value>().await {
                        let models: Vec<String> = data
                            .get("data")
                            .and_then(|d| d.as_array())
                            .map(|arr| {
                                arr.iter()
                                    .filter_map(|m| m.get("id").and_then(|id| id.as_str()).map(String::from))
                                    .collect()
                            })
                            .unwrap_or_default();
                        info["online"] = json!(true);
                        info["models"] = json!(models);
                    }
                }
                Ok(resp) => {
                    info["error"] = json!(format!("HTTP {}", resp.status()));
                }
                Err(e) => {
                    info["error"] = json!(e.to_string());
                }
            }
        }
        Err(e) => {
            info["error"] = json!(e.to_string());
        }
    }

    Json(info)
}
