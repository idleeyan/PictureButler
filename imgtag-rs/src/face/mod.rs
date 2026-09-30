//! Face recognition module: SCRFD detection + ArcFace recognition.
//!
//! Replaces insightface Python package. Loads ONNX models from buffalo_l.

pub mod aligner;
pub mod cluster;
pub mod detector;
pub mod preprocess;
pub mod recognizer;

use std::path::Path;

use parking_lot::Mutex;

use crate::config::AppConfig;
use crate::error::AppResult;

/// A detected face with bbox, keypoints, and score (before recognition).
#[derive(Debug, Clone)]
pub struct DetectedFace {
    pub bbox: [i64; 4],      // x1, y1, x2, y2 (original image coords)
    pub kps: [[f32; 2]; 5],  // 5 facial landmarks (original image coords)
    pub score: f32,
}

/// A fully processed face with embedding (after recognition).
#[derive(Debug, Clone)]
pub struct FaceInfo {
    pub bbox: [i64; 4],
    pub embedding: Vec<f32>,
    pub score: f32,
}

/// Main face recognition application (lazy-loads ONNX models).
pub struct FaceApp {
    config: AppConfig,
    inner: Mutex<FaceAppInner>,
}

struct FaceAppInner {
    detector: Option<detector::FaceDetector>,
    recognizer: Option<recognizer::FaceRecognizer>,
    failed: bool,
}

impl FaceApp {
    pub fn new(config: &AppConfig) -> Self {
        Self {
            config: config.clone(),
            inner: Mutex::new(FaceAppInner {
                detector: None,
                recognizer: None,
                failed: false,
            }),
        }
    }

    /// Borrow the inner config (for reading settings).
    pub fn config_ref(&self) -> &AppConfig {
        &self.config
    }

    /// Detect faces in an image and extract embeddings.
    /// Returns a list of FaceInfo with bbox, embedding, and score.
    pub fn detect_faces(&self, filepath: &Path) -> AppResult<Vec<FaceInfo>> {
        let settings = self.config.settings.read().clone();
        if !settings.face_enabled {
            return Ok(vec![]);
        }

        let mut inner = self.inner.lock();
        if inner.failed {
            return Ok(vec![]);
        }

        // Lazy-load models
        if inner.detector.is_none() {
            let model_dir = self.config.models_buffalo_l_dir();
            if !model_dir.exists() {
                crate::warn(&format!("Model directory not found: {}", model_dir.display()));
                crate::warn("Face recognition will be unavailable");
                inner.failed = true;
                return Ok(vec![]);
            }

            crate::info("Loading face detection models (buffalo_l)...");
            match detector::FaceDetector::new(&model_dir) {
                Ok(d) => inner.detector = Some(d),
                Err(e) => {
                    crate::warn(&format!("Failed to load face detector: {}", e));
                    inner.failed = true;
                    return Ok(vec![]);
                }
            }
            match recognizer::FaceRecognizer::new(&model_dir) {
                Ok(r) => inner.recognizer = Some(r),
                Err(e) => {
                    crate::warn(&format!("Failed to load face recognizer: {}", e));
                    inner.failed = true;
                    return Ok(vec![]);
                }
            }
            crate::info("Face models loaded (CPU mode)");
        }

        // Step 1: Detect faces (use scoped mutable borrow)
        let raw_faces = {
            let det = inner.detector.as_mut().unwrap();
            det.detect(
                filepath,
                settings.face_det_thresh as f32,
                settings.face_min_size as i64,
            )?
        };

        if raw_faces.is_empty() {
            return Ok(vec![]);
        }

        // Step 2: Decode image for alignment (RgbImage, as aligner expects &RgbImage)
        let img = crate::imageproc::io::decode_rgb8(filepath)?;

        // Step 3: For each face, align + recognize
        let mut results = Vec::with_capacity(raw_faces.len());
        for face in raw_faces {
            // Check min size (filter small faces)
            let w = face.bbox[2] - face.bbox[0];
            let h = face.bbox[3] - face.bbox[1];
            if w < settings.face_min_size || h < settings.face_min_size {
                continue;
            }

            // Align face to 112x112 using 5-point landmarks
            let aligned = aligner::align_face(&img, &face.kps);

            // Extract 512-d embedding (scoped mutable borrow)
            let embedding = {
                let rec = inner.recognizer.as_mut().unwrap();
                rec.extract_embedding(&aligned)?
            };

            results.push(FaceInfo {
                bbox: face.bbox,
                embedding,
                score: face.score,
            });
        }

        Ok(results)
    }

    /// Reset cached models (called when settings change).
    pub fn reset(&self) {
        let mut inner = self.inner.lock();
        inner.detector = None;
        inner.recognizer = None;
        inner.failed = false;
    }
}
