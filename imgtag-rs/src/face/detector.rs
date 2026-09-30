//! SCRFD face detector (replaces insightFace FaceAnalysis + SCRFD).
//!
//! Loads det_10g.onnx and runs SCRFD detection with 5-point landmarks.
//!
//! Model: SCRFD_10G
//! - Input:  1x3x640x640 (BGR, letterbox resized, normalized (x-127.5)/127.5)
//! - Output: 9 tensors (3 strides × 3 outputs: scores, bboxes, kps)
//!   - Output order (grouped by type): score_8, score_16, score_32, bbox_8, bbox_16, bbox_32, kps_8, kps_16, kps_32
//!   - Each grid position has 2 anchors: stride 8 → 12800, stride 16 → 3200, stride 32 → 800
//!   - Anchor layout (spatial-first): idx = (y*W + x) * numAnchors + a

use std::path::Path;

use ndarray::Axis;
use ort::session::{builder::GraphOptimizationLevel, Session};
use ort::value::Tensor;

use crate::error::{AppError, AppResult};
use crate::face::preprocess::{self, DET_INPUT_SIZE};
use crate::face::DetectedFace;

/// Strides for SCRFD anchor-based detection.
const STRIDES: [u32; 3] = [8, 16, 32];
/// NMS IoU threshold (matching insightface default).
const NMS_IOU_THRESH: f32 = 0.4;

pub struct FaceDetector {
    session: Session,
}

impl FaceDetector {
    /// Load the SCRFD detector model from the buffalo_l directory.
    pub fn new(model_dir: &Path) -> AppResult<Self> {
        let model_path = model_dir.join("det_10g.onnx");
        if !model_path.exists() {
            return Err(AppError::other(format!(
                "Detector model not found: {}",
                model_path.display()
            )));
        }

        let session = Session::builder()?
            .with_optimization_level(GraphOptimizationLevel::Level3)?
            .with_intra_threads(4)?
            .commit_from_file(&model_path)?;

        Ok(Self { session })
    }

    /// Detect faces in an image.
    ///
    /// Returns a list of DetectedFace with bbox, 5-point landmarks, and score.
    /// Faces with score < det_thresh are filtered out.
    pub fn detect(
        &mut self,
        filepath: &Path,
        det_thresh: f32,
        _min_size: i64,
    ) -> AppResult<Vec<DetectedFace>> {
        // Decode image
        let img = crate::imageproc::io::decode_rgb8(filepath)?;
        let (orig_w, orig_h) = (img.width(), img.height());

        // Preprocess: letterbox resize to 640x640, BGR, raw pixel values
        let pre_result = preprocess::preprocess_for_detection(&img);

        // Add batch dimension: (3, H, W) -> (1, 3, H, W)
        let input_4d = pre_result.tensor.insert_axis(Axis(0));

        // Flatten to (data, shape) for ort (Array4 doesn't implement OwnedTensorArrayData)
        let shape = vec![
            1i64,
            3,
            DET_INPUT_SIZE as i64,
            DET_INPUT_SIZE as i64,
        ];
        let flat_data: Vec<f32> = input_4d.iter().cloned().collect();

        // Run inference (ort 2.0.0-rc.13 API)
        let input_value = Tensor::from_array((shape, flat_data))?;
        let outputs = self.session.run(ort::inputs![input_value])?;

        // Post-process: decode anchors, filter, NMS, scale back to original image coords
        let faces = postprocess(
            &outputs,
            det_thresh,
            orig_w,
            orig_h,
            pre_result.ratio,
            pre_result.pad_w,
            pre_result.pad_h,
        )?;
        Ok(faces)
    }
}

/// Post-process SCRFD outputs: decode anchors, filter by score, NMS.
fn postprocess(
    outputs: &ort::session::SessionOutputs,
    det_thresh: f32,
    orig_w: u32,
    orig_h: u32,
    ratio: f32,
    pad_w: u32,
    pad_h: u32,
) -> AppResult<Vec<DetectedFace>> {
        let mut candidates = Vec::new();

        // SCRFD det_10g 实际输出顺序（按类型分组，非按 stride 分组）：
        //   score_8, score_16, score_32, bbox_8, bbox_16, bbox_32, kps_8, kps_16, kps_32
        // 每位置 2 个 anchor：stride 8 → 12800，stride 16 → 3200，stride 32 → 800
        // Anchor 布局（spatial-first）：idx = (y*W + x) * numAnchors + a
        const NUM_ANCHORS_PER_POS: usize = 2;

        for (i, &stride) in STRIDES.iter().enumerate() {
            let feature_size = (DET_INPUT_SIZE / stride) as usize;
            let num_anchors = feature_size * feature_size * NUM_ANCHORS_PER_POS;

            let score_idx = i;        // 0, 1, 2
            let bbox_idx = i + 3;     // 3, 4, 5
            let kps_idx = i + 6;      // 6, 7, 8

            // Extract tensors (ort 2.0.0-rc.13: try_extract_tensor returns (&Shape, &[f32]))
            let (_scores_shape, scores_data) = match outputs[score_idx].try_extract_tensor::<f32>() {
                Ok(t) => t,
                Err(_) => continue,
            };
            let (_bboxes_shape, bboxes_data) = match outputs[bbox_idx].try_extract_tensor::<f32>() {
                Ok(t) => t,
                Err(_) => continue,
            };
            let (_kps_shape, kps_data) = match outputs[kps_idx].try_extract_tensor::<f32>() {
                Ok(t) => t,
                Err(_) => continue,
            };

            // Iterate over anchors
            for j in 0..num_anchors {
                // Score: shape (1, N, 1) or (N, 1) or (N,)
                let score = scores_data.get(j).copied().unwrap_or(0.0);

                if score < det_thresh {
                    continue;
                }

                // Anchor center in input image coords
                // 关键：j 是 anchor 级索引（已翻倍），需先除以 NUM_ANCHORS_PER_POS 得到网格位置
                // pos = j / 2，对应网格索引 (y*W + x)
                let pos = j / NUM_ANCHORS_PER_POS;
                let cx = ((pos % feature_size) as f32 + 0.5) * stride as f32;
                let cy = ((pos / feature_size) as f32 + 0.5) * stride as f32;

                // Decode bbox: [l, t, r, b] offsets from anchor center
                let bbox_data: [f32; 4] = [
                    bboxes_data.get(j * 4).copied().unwrap_or(0.0),
                    bboxes_data.get(j * 4 + 1).copied().unwrap_or(0.0),
                    bboxes_data.get(j * 4 + 2).copied().unwrap_or(0.0),
                    bboxes_data.get(j * 4 + 3).copied().unwrap_or(0.0),
                ];

                let l = bbox_data[0] * stride as f32;
                let t = bbox_data[1] * stride as f32;
                let r = bbox_data[2] * stride as f32;
                let b = bbox_data[3] * stride as f32;

                let x1 = cx - l;
                let y1 = cy - t;
                let x2 = cx + r;
                let y2 = cy + b;

                // Decode 5-point keypoints
                let mut kps_arr = [[0.0f32; 2]; 5];
                for k in 0..5 {
                    let kx = kps_data.get(j * 10 + k * 2).copied().unwrap_or(0.0);
                    let ky = kps_data.get(j * 10 + k * 2 + 1).copied().unwrap_or(0.0);
                    kps_arr[k][0] = cx + kx * stride as f32;
                    kps_arr[k][1] = cy + ky * stride as f32;
                }

                // Scale from letterbox input coords to original image coords
                // 先减去 padding，再除以缩放比例（与 C# FaceOnnxEngine 一致）
                let x1_orig = (x1 - pad_w as f32) / ratio;
                let y1_orig = (y1 - pad_h as f32) / ratio;
                let x2_orig = (x2 - pad_w as f32) / ratio;
                let y2_orig = (y2 - pad_h as f32) / ratio;

                // Clip to image bounds
                candidates.push(DetectedFace {
                    bbox: [
                        x1_orig.max(0.0) as i64,
                        y1_orig.max(0.0) as i64,
                        x2_orig.min(orig_w as f32) as i64,
                        y2_orig.min(orig_h as f32) as i64,
                    ],
                    kps: [
                        [(kps_arr[0][0] - pad_w as f32) / ratio, (kps_arr[0][1] - pad_h as f32) / ratio],
                        [(kps_arr[1][0] - pad_w as f32) / ratio, (kps_arr[1][1] - pad_h as f32) / ratio],
                        [(kps_arr[2][0] - pad_w as f32) / ratio, (kps_arr[2][1] - pad_h as f32) / ratio],
                        [(kps_arr[3][0] - pad_w as f32) / ratio, (kps_arr[3][1] - pad_h as f32) / ratio],
                        [(kps_arr[4][0] - pad_w as f32) / ratio, (kps_arr[4][1] - pad_h as f32) / ratio],
                    ],
                    score,
                });
            }
        }

        // NMS
        nms(&mut candidates, NMS_IOU_THRESH);
        Ok(candidates)
    }

/// Non-Maximum Suppression: remove overlapping detections.
fn nms(faces: &mut Vec<DetectedFace>, iou_thresh: f32) {
    // Sort by score descending
    faces.sort_by(|a, b| {
        b.score
            .partial_cmp(&a.score)
            .unwrap_or(std::cmp::Ordering::Equal)
    });

    let mut keep = Vec::with_capacity(faces.len());
    while !faces.is_empty() {
        let best = faces.remove(0);
        keep.push(best.clone());
        faces.retain(|f| iou(&best.bbox, &f.bbox) < iou_thresh);
    }
    *faces = keep;
}

/// Compute IoU (Intersection over Union) of two bounding boxes.
fn iou(a: &[i64; 4], b: &[i64; 4]) -> f32 {
    let x1 = a[0].max(b[0]);
    let y1 = a[1].max(b[1]);
    let x2 = a[2].min(b[2]);
    let y2 = a[3].min(b[3]);

    let inter_w = (x2 - x1).max(0);
    let inter_h = (y2 - y1).max(0);
    let inter = (inter_w * inter_h) as f32;

    let area_a = ((a[2] - a[0]) * (a[3] - a[1])) as f32;
    let area_b = ((b[2] - b[0]) * (b[3] - b[1])) as f32;
    let union = area_a + area_b - inter;

    if union > 0.0 {
        inter / union
    } else {
        0.0
    }
}
