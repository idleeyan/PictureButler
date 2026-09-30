//! Image preprocessing for ONNX inference.
//!
//! Mirrors insightface's preprocessing: BGR conversion, letterbox resize, normalize.
//! SCRFD det_10g input: 1x3x640x640, BGR, normalized (x - 127.5) / 127.5
//! ArcFace w600k_r50 input: 1x3x112x112, RGB, normalized (x - 127.5) / 127.5

use image::RgbImage;
use ndarray::Array3;

/// SCRFD detection input size.
pub const DET_INPUT_SIZE: u32 = 640;

/// ArcFace recognition input size.
pub const REC_INPUT_SIZE: u32 = 112;

/// Result of detection preprocessing: the CHW tensor and letterbox metadata.
pub struct DetPreprocessResult {
    pub tensor: Array3<f32>,
    pub ratio: f32,
    pub pad_w: u32,
    pub pad_h: u32,
}

/// Preprocess an image for SCRFD detection.
/// Returns a CHW tensor (BGR, raw 0-255 values) plus letterbox metadata.
///
/// Steps (matching insightface SCRFD and C# FaceOnnxEngine):
/// 1. Convert RGB to BGR
/// 2. Letterbox resize: scale so the longer side fits 640, pad the shorter side with gray
/// 3. Raw pixel values (no normalization — SCRFD expects 0-255 BGR)
/// 4. Transpose HWC -> CHW
pub fn preprocess_for_detection(img: &RgbImage) -> DetPreprocessResult {
    let (w, h) = img.dimensions();
    let target = DET_INPUT_SIZE;

    // Letterbox: scale to fit, pad with gray (127)
    let ratio = (target as f32 / w as f32).min(target as f32 / h as f32);
    // 必须 clamp：浮点误差可能让 round 得到 641，导致下面 (target - new_w) 发生 u32 下溢
    let new_w = ((w as f32 * ratio).round() as u32).min(target);
    let new_h = ((h as f32 * ratio).round() as u32).min(target);
    let pad_w = (target - new_w) / 2;
    let pad_h = (target - new_h) / 2;

    // Resize to new_w x new_h
    let resized = resize_bilinear(img, new_w, new_h);

    // Create padded canvas (gray = 127)
    let mut padded = RgbImage::from_pixel(target, target, image::Rgb([127, 127, 127]));
    // Paste resized image at (pad_w, pad_h)
    for y in 0..new_h {
        for x in 0..new_w {
            let pixel = resized.get_pixel(x, y);
            padded.put_pixel(pad_w + x, pad_h + y, *pixel);
        }
    }

    // Convert to BGR, normalize, and arrange as CHW
    // 归一化必须与 C# FaceOnnxEngine 及 insightface 保持一致：(x - 127.5) / 127.5
    // （insightface 用 1/128，差异可忽略；但**不能不做归一化**——直接喂 0-255 会让模型
    //   输入分布完全偏离训练分布，检测框会明显劣化：实测均值 score 0.848→0.772，
    //   且会把人脸框成异常小框。）
    let mut tensor = Array3::<f32>::zeros((3, target as usize, target as usize));
    for y in 0..target as usize {
        for x in 0..target as usize {
            let pixel = padded.get_pixel(x as u32, y as u32);
            // BGR order: Blue=0, Green=1, Red=2 (reversed from RGB)
            let b = pixel[2] as f32;
            let g = pixel[1] as f32;
            let r = pixel[0] as f32;
            tensor[[0, y, x]] = (b - 127.5) / 127.5;
            tensor[[1, y, x]] = (g - 127.5) / 127.5;
            tensor[[2, y, x]] = (r - 127.5) / 127.5;
        }
    }

    DetPreprocessResult {
        tensor,
        ratio,
        pad_w,
        pad_h,
    }
}

/// Preprocess an aligned 112x112 face image for ArcFace recognition.
/// Returns a 1x3x112x112 tensor in NCHW format, RGB, normalized.
///
/// Steps (matching insightface arcface_onnx.py):
/// 1. Input is already 112x112 RGB
/// 2. Normalize: (x - 127.5) / 127.5
/// 3. Transpose HWC -> CHW
/// 4. Add batch dimension
pub fn preprocess_for_recognition(img: &RgbImage) -> Array3<f32> {
    let (w, h) = img.dimensions();
    debug_assert_eq!(w, REC_INPUT_SIZE);
    debug_assert_eq!(h, REC_INPUT_SIZE);

    let mut tensor = Array3::<f32>::zeros((3, REC_INPUT_SIZE as usize, REC_INPUT_SIZE as usize));
    for y in 0..REC_INPUT_SIZE as usize {
        for x in 0..REC_INPUT_SIZE as usize {
            let pixel = img.get_pixel(x as u32, y as u32);
            // RGB order for recognition
            let r = pixel[0] as f32;
            let g = pixel[1] as f32;
            let b = pixel[2] as f32;
            tensor[[0, y, x]] = (r - 127.5) / 127.5;
            tensor[[1, y, x]] = (g - 127.5) / 127.5;
            tensor[[2, y, x]] = (b - 127.5) / 127.5;
        }
    }
    tensor
}

/// Bilinear resize an RgbImage (matching cv2.INTER_LINEAR).
pub fn resize_bilinear(img: &RgbImage, dst_w: u32, dst_h: u32) -> RgbImage {
    let (src_w, src_h) = img.dimensions();
    if src_w == dst_w && src_h == dst_h {
        return img.clone();
    }

    let mut result = RgbImage::new(dst_w, dst_h);
    let x_ratio = (src_w - 1) as f32 / dst_w.max(1) as f32;
    let y_ratio = (src_h - 1) as f32 / dst_h.max(1) as f32;

    for y in 0..dst_h {
        for x in 0..dst_w {
            let sx = x as f32 * x_ratio;
            let sy = y as f32 * y_ratio;
            let x0 = sx.floor() as u32;
            let y0 = sy.floor() as u32;
            let x1 = (x0 + 1).min(src_w - 1);
            let y1 = (y0 + 1).min(src_h - 1);
            let dx = sx - x0 as f32;
            let dy = sy - y0 as f32;

            let p00 = img.get_pixel(x0, y0);
            let p01 = img.get_pixel(x0, y1);
            let p10 = img.get_pixel(x1, y0);
            let p11 = img.get_pixel(x1, y1);

            let mut pixel = image::Rgb([0, 0, 0]);
            for c in 0..3 {
                let v00 = p00[c] as f32;
                let v01 = p01[c] as f32;
                let v10 = p10[c] as f32;
                let v11 = p11[c] as f32;
                let v = v00 * (1.0 - dx) * (1.0 - dy)
                    + v10 * dx * (1.0 - dy)
                    + v01 * (1.0 - dx) * dy
                    + v11 * dx * dy;
                pixel[c] = v.round().clamp(0.0, 255.0) as u8;
            }
            result.put_pixel(x, y, pixel);
        }
    }
    result
}
