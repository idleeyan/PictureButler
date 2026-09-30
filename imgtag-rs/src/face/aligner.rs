//! Face alignment using 5-point landmarks (replaces insightface arcface_onnx.py).
//!
//! Implements Umeyama similarity transform to align faces to 112x112.

use image::{Rgb, RgbImage};

use crate::face::preprocess::REC_INPUT_SIZE;

/// Standard ArcFace 5-point template (112x112 target).
/// From insightface/python-package/insightface/utils/face_align.py
const ARCFACE_DST: [[f32; 2]; 5] = [
    [38.2946, 51.6963],
    [73.5318, 51.5014],
    [56.0252, 71.7366],
    [41.5493, 92.3655],
    [70.7299, 92.2041],
];

/// Align a face using 5-point landmarks to a 112x112 image.
///
/// Computes the similarity transform (rotation + scale + translation)
/// that maps the source landmarks to the ArcFace template, then applies
/// the inverse transform to warp the source image.
pub fn align_face(img: &RgbImage, kps: &[[f32; 2]; 5]) -> RgbImage {
    // Compute similarity transform: src -> dst
    let (scale, rotation, tx, ty) = estimate_similarity_transform(kps, &ARCFACE_DST);

    // Build affine matrix M (2x3) for forward transform: src -> dst
    // M = [[s*cos, -s*sin, tx], [s*sin, s*cos, ty]]
    let cos = rotation.cos();
    let sin = rotation.sin();
    let m = [
        [scale * cos, -scale * sin, tx],
        [scale * sin, scale * cos, ty],
    ];

    // Warp affine (inverse mapping): for each dst pixel, find src pixel
    warp_affine(img, &m, REC_INPUT_SIZE, REC_INPUT_SIZE)
}

/// Estimate the similarity transform (scale, rotation, translation) from src to dst.
///
/// Uses the closed-form solution for 2D similarity transform (Umeyama algorithm).
/// Returns (scale, theta, tx, ty) where:
/// - R = [[cos(theta), -sin(theta)], [sin(theta), cos(theta)]]
/// - dst = scale * R * src + t
fn estimate_similarity_transform(
    src: &[[f32; 2]; 5],
    dst: &[[f32; 2]; 5],
) -> (f32, f32, f32, f32) {
    // Step 1: Compute centroids
    let src_mean = centroid(src);
    let dst_mean = centroid(dst);

    // Step 2: Compute centered vectors
    let src_centered: Vec<[f32; 2]> = src.iter().map(|p| [p[0] - src_mean[0], p[1] - src_mean[1]]).collect();
    let dst_centered: Vec<[f32; 2]> = dst.iter().map(|p| [p[0] - dst_mean[0], p[1] - dst_mean[1]]).collect();

    // Step 3: Compute sums for scale and rotation
    // a = sum(src_x * dst_x + src_y * dst_y)  (dot product sum)
    // b = sum(src_x * dst_y - src_y * dst_x)  (cross product sum)
    // S_pp = sum(src_x^2 + src_y^2)  (source variance)
    let mut a = 0.0f32;
    let mut b = 0.0f32;
    let mut s_pp = 0.0f32;

    for i in 0..5 {
        a += src_centered[i][0] * dst_centered[i][0] + src_centered[i][1] * dst_centered[i][1];
        b += src_centered[i][0] * dst_centered[i][1] - src_centered[i][1] * dst_centered[i][0];
        s_pp += src_centered[i][0] * src_centered[i][0] + src_centered[i][1] * src_centered[i][1];
    }

    // Step 4: Compute scale and rotation
    let scale = if s_pp > 0.0 {
        (a * a + b * b).sqrt() / s_pp
    } else {
        1.0
    };
    let theta = b.atan2(a);

    // Step 5: Compute translation
    // t = dst_mean - scale * R * src_mean
    let cos = theta.cos();
    let sin = theta.sin();
    let tx = dst_mean[0] - scale * (cos * src_mean[0] - sin * src_mean[1]);
    let ty = dst_mean[1] - scale * (sin * src_mean[0] + cos * src_mean[1]);

    (scale, theta, tx, ty)
}

/// Compute the centroid of a set of 2D points.
fn centroid(points: &[[f32; 2]; 5]) -> [f32; 2] {
    let mut cx = 0.0f32;
    let mut cy = 0.0f32;
    for p in points.iter() {
        cx += p[0];
        cy += p[1];
    }
    [cx / 5.0, cy / 5.0]
}

/// Apply affine transform using inverse mapping (matches cv2.warpAffine).
///
/// M is the 2x3 forward transform matrix. For each destination pixel,
/// we compute the source pixel via M^{-1}.
fn warp_affine(src: &RgbImage, m: &[[f32; 3]; 2], dst_w: u32, dst_h: u32) -> RgbImage {
    // Compute inverse transform: M^{-1}
    // M = [[a, b, c], [d, e, f]]
    // det = a*e - b*d
    // M^{-1} = [[e, -b, (b*f - c*e)], [-d, a, (c*d - a*f)]] / det
    let a = m[0][0];
    let b = m[0][1];
    let c = m[0][2];
    let d = m[1][0];
    let e = m[1][1];
    let f = m[1][2];

    let det = a * e - b * d;
    if det.abs() < 1e-6 {
        // Degenerate transform; return black image
        return RgbImage::new(dst_w, dst_h);
    }

    let inv_det = 1.0 / det;
    // Inverse rotation/scale part
    let inv_a = e * inv_det;
    let inv_b = -b * inv_det;
    let inv_d = -d * inv_det;
    let inv_e = a * inv_det;
    // Inverse translation part
    let inv_c = (b * f - c * e) * inv_det;
    let inv_f = (c * d - a * f) * inv_det;

    let (src_w, src_h) = src.dimensions();
    let mut dst = RgbImage::new(dst_w, dst_h);

    for y in 0..dst_h {
        for x in 0..dst_w {
            // Inverse map: dst (x, y) -> src (sx, sy)
            let sx = inv_a * x as f32 + inv_b * y as f32 + inv_c;
            let sy = inv_d * x as f32 + inv_e * y as f32 + inv_f;

            // Bilinear interpolation
            let pixel = sample_bilinear(src, sx, sy, src_w, src_h);
            dst.put_pixel(x, y, pixel);
        }
    }

    dst
}

/// Bilinear sampling from source image at floating-point coordinates.
fn sample_bilinear(src: &RgbImage, x: f32, y: f32, w: u32, h: u32) -> Rgb<u8> {
    if x < 0.0 || y < 0.0 || x >= w as f32 - 1.0 || y >= h as f32 - 1.0 {
        // Out of bounds: return black (or could clamp)
        if x < 0.0 || y < 0.0 || x >= w as f32 || y >= h as f32 {
            return Rgb([0, 0, 0]);
        }
        // Edge case: use nearest pixel
        let px = x.round() as u32;
        let py = y.round() as u32;
        let px = px.min(w - 1);
        let py = py.min(h - 1);
        return *src.get_pixel(px, py);
    }

    let x0 = x.floor() as u32;
    let y0 = y.floor() as u32;
    let x1 = x0 + 1;
    let y1 = y0 + 1;

    let dx = x - x0 as f32;
    let dy = y - y0 as f32;

    let p00 = src.get_pixel(x0, y0);
    let p01 = src.get_pixel(x0, y1);
    let p10 = src.get_pixel(x1, y0);
    let p11 = src.get_pixel(x1, y1);

    let mut result = [0u8; 3];
    for c in 0..3 {
        let v00 = p00[c] as f32;
        let v01 = p01[c] as f32;
        let v10 = p10[c] as f32;
        let v11 = p11[c] as f32;

        let v = v00 * (1.0 - dx) * (1.0 - dy)
            + v10 * dx * (1.0 - dy)
            + v01 * (1.0 - dx) * dy
            + v11 * dx * dy;

        result[c] = v.round().clamp(0.0, 255.0) as u8;
    }

    Rgb(result)
}
