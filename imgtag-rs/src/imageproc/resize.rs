//! Image resizing with Lanczos filter (replaces PIL Image.thumbnail).

use std::num::NonZeroU32;

use image::{DynamicImage, GenericImageView, RgbImage};
use fast_image_resize as fr;

/// Resize an image so the longest side <= max_size, preserving aspect ratio.
/// Uses Lanczos3 filter for high quality (matching PIL's Image.LANCZOS).
pub fn thumbnail(img: &DynamicImage, max_size: u32) -> DynamicImage {
    let (w, h) = img.dimensions();
    if w <= max_size && h <= max_size {
        return img.clone();
    }
    let (nw, nh) = compute_thumb_size(w, h, max_size);
    resize_lanczos(img, nw, nh)
}

/// Resize an RgbImage using fast_image_resize (Lanczos3).
pub fn resize_lanczos_rgb(img: &RgbImage, nw: u32, nh: u32) -> RgbImage {
    let (w, h) = (img.width(), img.height());
    if w == nw && h == nh {
        return img.clone();
    }
    // Fallback if dimensions are zero
    if w == 0 || h == 0 || nw == 0 || nh == 0 {
        return image::imageops::resize(img, nw, nh, image::imageops::FilterType::Lanczos3);
    }

    let src_w = match NonZeroU32::new(w) {
        Some(v) => v,
        None => return image::imageops::resize(img, nw, nh, image::imageops::FilterType::Lanczos3),
    };
    let src_h = match NonZeroU32::new(h) {
        Some(v) => v,
        None => return image::imageops::resize(img, nw, nh, image::imageops::FilterType::Lanczos3),
    };
    let dst_w = match NonZeroU32::new(nw) {
        Some(v) => v,
        None => return image::imageops::resize(img, nw, nh, image::imageops::FilterType::Lanczos3),
    };
    let dst_h = match NonZeroU32::new(nh) {
        Some(v) => v,
        None => return image::imageops::resize(img, nw, nh, image::imageops::FilterType::Lanczos3),
    };

    // Src image (v3: from_vec_u8 returns Result)
    let src = match fr::Image::from_vec_u8(
        src_w,
        src_h,
        img.as_raw().clone(),
        fr::PixelType::U8x3,
    ) {
        Ok(s) => s,
        Err(_) => {
            return image::imageops::resize(img, nw, nh, image::imageops::FilterType::Lanczos3);
        }
    };

    // Dst image (v3: new returns Image directly, not Result)
    let mut dst = fr::Image::new(dst_w, dst_h, fr::PixelType::U8x3);

    // Resizer with Lanczos3 convolution filter (v3: new takes ResizeAlg)
    let alg = fr::ResizeAlg::Convolution(fr::FilterType::Lanczos3);
    let mut resizer = fr::Resizer::new(alg);

    // v3: resize takes &DynamicImageView; view()/view_mut() return views directly
    let src_view = src.view();
    let mut dst_view = dst.view_mut();

    if resizer.resize(&src_view, &mut dst_view).is_err() {
        return image::imageops::resize(img, nw, nh, image::imageops::FilterType::Lanczos3);
    }

    match RgbImage::from_raw(nw, nh, dst.buffer().to_vec()) {
        Some(img) => img,
        None => image::imageops::resize(img, nw, nh, image::imageops::FilterType::Lanczos3),
    }
}

/// Resize a DynamicImage using fast_image_resize.
pub fn resize_lanczos(img: &DynamicImage, nw: u32, nh: u32) -> DynamicImage {
    let rgb = img.to_rgb8();
    let resized = resize_lanczos_rgb(&rgb, nw, nh);
    DynamicImage::ImageRgb8(resized)
}

/// Compute thumbnail dimensions preserving aspect ratio.
pub fn compute_thumb_size(w: u32, h: u32, max_size: u32) -> (u32, u32) {
    if w == 0 || h == 0 {
        return (max_size, max_size);
    }
    let max_w = w.max(h);
    if max_w <= max_size {
        return (w, h);
    }
    let ratio = max_size as f64 / max_w as f64;
    let nw = (w as f64 * ratio).round() as u32;
    let nh = (h as f64 * ratio).round() as u32;
    (nw.max(1), nh.max(1))
}
