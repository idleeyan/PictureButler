//! Thumbnail disk cache (mirrors Python /api/thumbnail with .thumbs/ caching).

use std::path::{Path, PathBuf};

use crate::error::AppResult;
use crate::imageproc::{io, resize};
use crate::utils::hash;

/// Get or create a cached thumbnail for the given image path and size.
/// Returns the path to the cached JPEG file.
pub fn get_or_create(path: &Path, size: u32, cache_dir: &Path) -> AppResult<PathBuf> {
    let st = std::fs::metadata(path)?;
    let mtime_ns = st
        .modified()?
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_nanos())
        .unwrap_or(0);
    let file_size = st.len();

    let key = hash::thumb_cache_key(
        &path.to_string_lossy(),
        mtime_ns,
        file_size,
        size,
    );
    let cache_file = cache_dir.join(format!("{}.jpg", key));

    if cache_file.exists() {
        return Ok(cache_file);
    }

    // Generate thumbnail
    let img = io::decode_rgb(path)?;
    let thumb = resize::thumbnail(&img, size);
    let jpeg_bytes = io::encode_jpeg(&thumb, 80);
    std::fs::write(&cache_file, &jpeg_bytes)?;

    Ok(cache_file)
}

/// Remove all cached thumbnails for a given image path (used when deleting/moving images).
pub fn clean_for_path(path: &str, cache_dir: &Path) {
    let prefix = hash::thumb_cache_key_prefix(path);
    if let Ok(entries) = std::fs::read_dir(cache_dir) {
        for entry in entries.flatten() {
            let name = entry.file_name();
            let name_str = name.to_string_lossy();
            if name_str.starts_with(&prefix) {
                let _ = std::fs::remove_file(entry.path());
            }
        }
    }
}
