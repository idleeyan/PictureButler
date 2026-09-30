//! Utility functions: tag splitting, hashing, SSE helpers, logging re-exports.

pub mod hash;
pub mod sse;
pub mod trash;

/// Split a comma/semicolon/whitespace-separated tags string into a list.
/// Mirrors _split_tags in main.py.
pub fn split_tags(tags_str: &str) -> Vec<String> {
    if tags_str.is_empty() {
        return vec![];
    }
    // Split on commas (both ASCII and CJK), semicolons, and whitespace
    let parts = tags_str.split(|c: char| {
        c == ',' || c == '，' || c == ';' || c == '；' || c.is_whitespace()
    });
    parts
        .map(|p| p.trim().to_string())
        .filter(|p| !p.is_empty())
        .collect()
}

/// Generate the next "Person N" name (mirrors _next_person_name).
pub fn next_person_name(existing_names: &[String]) -> String {
    let re = regex::Regex::new(r"^人物(\d+)$").unwrap();
    let mut used: std::collections::HashSet<i64> = std::collections::HashSet::new();
    for name in existing_names {
        if let Some(caps) = re.captures(name) {
            if let Ok(n) = caps[1].parse::<i64>() {
                used.insert(n);
            }
        }
    }
    let mut n = 1i64;
    while used.contains(&n) {
        n += 1;
    }
    format!("人物{}", n)
}

/// Supported image file extensions (mirrors SUPPORTED_EXTS).
pub const SUPPORTED_EXTS: &[&str] = &[".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif"];

/// Check if a path has a supported image extension.
pub fn is_supported_image(path: &std::path::Path) -> bool {
    path.extension()
        .and_then(|ext| ext.to_str())
        .map(|ext| {
            let lower = ext.to_lowercase();
            SUPPORTED_EXTS.iter().any(|e| *e == lower.as_str())
        })
        .unwrap_or(false)
}

/// Recursively collect image files in a directory (mirrors _collect_image_files).
pub fn collect_image_files(folder: &std::path::Path) -> Vec<std::path::PathBuf> {
    let mut images = Vec::new();
    let _ = walkdir::WalkDir::new(folder)
        .into_iter()
        .filter_map(|e| e.ok())
        .for_each(|entry| {
            if entry.file_type().is_file() && is_supported_image(entry.path()) {
                images.push(entry.path().to_path_buf());
            }
        });
    images.sort();
    images
}
