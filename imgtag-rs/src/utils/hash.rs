//! MD5 hashing for thumbnail cache keys (mirrors Python hashlib.md5 usage).

use md5::Context;

/// Generate a 16-char MD5 cache key for a thumbnail.
/// Format matches Python: `md5(f"{path}|{mtime_ns}|{size}|{thumb_size}".encode()).hexdigest()[:16]`
pub fn thumb_cache_key(path: &str, mtime_ns: u128, size: u64, thumb_size: u32) -> String {
    let mut hasher = Context::new();
    hasher.consume(format!("{}|{}|{}|{}", path, mtime_ns, size, thumb_size).as_bytes());
    let result = hasher.compute();
    let hex: String = result.iter().map(|b| format!("{:02x}", b)).collect();
    hex[..16].to_string()
}

/// Generate a cache key prefix for cleaning all cached thumbnails of a path.
/// Format matches Python: `md5(f"{path}|".encode()).hexdigest()[:16]`
pub fn thumb_cache_key_prefix(path: &str) -> String {
    let mut hasher = Context::new();
    hasher.consume(format!("{}|", path).as_bytes());
    let result = hasher.compute();
    let hex: String = result.iter().map(|b| format!("{:02x}", b)).collect();
    hex[..16].to_string()
}
