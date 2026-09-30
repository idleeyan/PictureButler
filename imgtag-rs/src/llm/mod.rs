//! LM Studio HTTP client (replaces analyze_image in main.py).
//!
//! Calls the OpenAI-compatible /v1/chat/completions endpoint with a vision payload.

use base64::Engine;
use serde::{Deserialize, Serialize};

use crate::error::{AppError, AppResult};
use crate::imageproc::{io, resize};

/// Check LM Studio connectivity.
pub async fn check_lm_studio(url: &str) -> AppResult<()> {
    let client = reqwest::Client::builder()
        .timeout(std::time::Duration::from_secs(5))
        .build()?;
    let resp = client.get(format!("{}/models", url.trim_end_matches('/'))).send().await?;
    if !resp.status().is_success() {
        return Err(AppError::other(format!("LM Studio returned status {}", resp.status())));
    }
    let data: serde_json::Value = resp.json().await?;
    let models: Vec<String> = data.get("data")
        .and_then(|d| d.as_array())
        .map(|arr| arr.iter().filter_map(|m| m.get("id").and_then(|id| id.as_str()).map(String::from)).collect())
        .unwrap_or_default();
    if models.is_empty() {
        crate::warn("LM Studio started but no model loaded; please load a vision model");
    } else {
        crate::info(&format!("LM Studio online, models: {}", models.join(", ")));
    }
    Ok(())
}

#[derive(Debug, Clone, Serialize, Deserialize)]
struct ChatResponse {
    choices: Vec<ChatChoice>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
struct ChatChoice {
    message: ChatMessage,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
struct ChatMessage {
    content: String,
}

/// Analyze an image and return description + tags.
pub async fn analyze_image(
    lm_url: &str,
    model: &str,
    filepath: &std::path::Path,
    max_size: (u32, u32),
) -> AppResult<(String, String)> {
    let b64 = encode_image(filepath, max_size)?;
    // 详细版描述提示词：要求 180-260 字的结构化段落（主体 / 场景 / 构图 / 光线色调 / 风格质感）。
    // 注意 max_tokens 需同步放大，否则长描述会被截断成非法 JSON。
    let prompt = "请仔细观察这张图片，并用中文回答，严格遵循以下 JSON 格式（不要包含 markdown 代码块标记，description 内不要出现换行符）：\n\n{\n  \"description\": \"对图片的详细描述，180-260 字，写成一个连贯段落，依次覆盖：①画面主体：人物数量、性别呈现、年龄感、外貌与发型、衣着与配饰、姿态动作、表情神态；②场景与环境：地点、背景元素、时间或天气感、环境物件；③构图与视角：景别、机位高度、主体在画面中的位置；④光线与色调：光源方向、明暗对比、主色调、整体氛围；⑤风格与质感：写实摄影/插画/CG 渲染、清晰度、镜头或绘画质感\",\n  \"tags\": \"逗号分隔的中文标签，8-15 个，按重要性排序，覆盖主体、场景、风格、色调、情绪、构图\"\n}";

    let payload = serde_json::json!({
        "model": model,
        "messages": [{
            "role": "user",
            "content": [
                {"type": "image_url", "image_url": {"url": format!("data:image/jpeg;base64,{}", b64)}},
                {"type": "text", "text": prompt},
            ],
        }],
        "temperature": 0.3,
        "max_tokens": 1200,
    });

    let client = reqwest::Client::builder()
        .timeout(std::time::Duration::from_secs(300))
        .build()?;
    let resp = client
        .post(format!("{}/chat/completions", lm_url.trim_end_matches('/')))
        .json(&payload)
        .send()
        .await?;

    if !resp.status().is_success() {
        let status = resp.status();
        let text = resp.text().await.unwrap_or_default();
        return Err(AppError::other(format!("LM Studio error {}: {}", status, text)));
    }

    let data: ChatResponse = resp.json().await?;
    let raw = data.choices
        .first()
        .ok_or_else(|| AppError::other("No choices in response"))?
        .message
        .content
        .trim()
        .to_string();

    // Clean markdown code blocks
    let cleaned = clean_markdown(&raw);

    // Parse JSON
    let (description, tags) = match serde_json::from_str::<serde_json::Value>(&cleaned) {
        Ok(v) => (
            v.get("description").and_then(|d| d.as_str()).unwrap_or("").to_string(),
            v.get("tags").and_then(|t| t.as_str()).unwrap_or("").to_string(),
        ),
        Err(_) => (cleaned.clone(), String::new()),
    };

    Ok((description, tags))
}

/// Encode an image as base64 JPEG, resized to max_size.
fn encode_image(filepath: &std::path::Path, max_size: (u32, u32)) -> AppResult<String> {
    let img = io::decode_rgb(filepath)?;
    let thumb = resize::thumbnail(&img, max_size.0.max(max_size.1));
    let jpeg_bytes = io::encode_jpeg(&thumb, 85);
    Ok(base64::engine::general_purpose::STANDARD.encode(&jpeg_bytes))
}

/// Remove markdown code block markers from a string.
fn clean_markdown(raw: &str) -> String {
    let mut s = raw.to_string();
    if s.starts_with("```") {
        // Remove first line (```json or ```)
        if let Some(idx) = s.find('\n') {
            s = s[idx + 1..].to_string();
        }
        if s.ends_with("```") {
            s = s[..s.len() - 3].to_string();
        }
        s = s.trim().to_string();
    }
    s
}
