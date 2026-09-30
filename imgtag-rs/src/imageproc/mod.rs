//! Image processing: decoding, resizing, EXIF, thumbnail caching.
//!
//! Replaces PIL (Pillow) functionality from main.py.

pub mod exif;
pub mod io;
pub mod resize;
pub mod thumb_cache;
