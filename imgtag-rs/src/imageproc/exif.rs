//! EXIF parsing for DateTimeOriginal (replaces PIL _getexif).

use std::fs::File;
use std::io::BufReader;
use std::path::Path;

use exif::{In, Tag, Value};

/// Extract DateTimeOriginal from EXIF.
/// Returns "YYYY:MM:DD HH:MM:SS" format (matching Python's exif.get(36867)).
pub fn extract_date_taken(path: &Path) -> Option<String> {
    let file = File::open(path).ok()?;
    let mut bufreader = BufReader::new(&file);
    let exifreader = exif::Reader::new();
    let exif_data = exifreader.read_from_container(&mut bufreader).ok()?;

    // DateTimeOriginal = tag 36867, DateTimeDigitized = tag 36868
    let dt = exif_data.get_field(Tag::DateTimeOriginal, In::PRIMARY)
        .or_else(|| exif_data.get_field(Tag::DateTimeDigitized, In::PRIMARY))?;

    if let Value::Ascii(ref vec) = dt.value {
        if let Some(s) = vec.first() {
            // EXIF format is already "YYYY:MM:DD HH:MM:SS\0"
            let s = String::from_utf8_lossy(s);
            return Some(s.trim_end_matches('\0').trim().to_string());
        }
    }
    None
}
