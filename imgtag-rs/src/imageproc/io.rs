//! Image decoding/encoding utilities (replaces PIL Image.open).

use std::io::Cursor;
use std::path::Path;

use image::{DynamicImage, ImageReader};

use crate::error::AppResult;

/// Read image dimensions without fully decoding.
pub fn read_dimensions(path: &Path) -> AppResult<(u32, u32)> {
    let (w, h) = ImageReader::open(path)?.with_guessed_format()?.into_dimensions()?;
    Ok((w, h))
}

/// Decode an image file into RGB.
pub fn decode_rgb(path: &Path) -> AppResult<DynamicImage> {
    let img = ImageReader::open(path)?
        .with_guessed_format()?
        .decode()?;
    Ok(img.to_rgb8().into())
}

/// Decode an image and convert to RGB8 buffer.
pub fn decode_rgb8(path: &Path) -> AppResult<image::RgbImage> {
    let img = ImageReader::open(path)?
        .with_guessed_format()?
        .decode()?;
    Ok(img.to_rgb8())
}

/// Encode an image as JPEG bytes.
pub fn encode_jpeg(img: &DynamicImage, quality: u8) -> Vec<u8> {
    let mut buf = Cursor::new(Vec::new());
    let mut encoder = image::codecs::jpeg::JpegEncoder::new_with_quality(&mut buf, quality);
    let _ = encoder.encode_image(img);
    buf.into_inner()
}

/// Read image and return (width, height, file_size).
pub fn read_image_meta(path: &Path) -> AppResult<(u32, u32, u64)> {
    let (w, h) = read_dimensions(path)?;
    let size = std::fs::metadata(path).map(|m| m.len()).unwrap_or(0);
    Ok((w, h, size))
}
