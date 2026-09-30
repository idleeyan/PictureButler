//! Trash (recycle bin) wrapper around the `trash` crate.

use std::path::Path;

pub fn send_to_trash(path: &Path) -> Result<(), trash::Error> {
    trash::delete(path)
}
