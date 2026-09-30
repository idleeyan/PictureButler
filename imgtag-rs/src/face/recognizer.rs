//! ArcFace face recognizer (replaces insightface arcface_onnx.py).
//!
//! Loads w600k_r50.onnx and extracts 512-d embeddings from aligned 112x112 faces.
//!
//! Model: arcface_r100_v1 (w600k_r50)
//! - Input:  1x3x112x112 (RGB, normalized (x-127.5)/127.5)
//! - Output: 1x512 (embedding, L2-normalized)

use std::path::Path;

use ndarray::Axis;
use ort::session::{builder::GraphOptimizationLevel, Session};
use ort::value::Tensor;

use crate::error::{AppError, AppResult};
use crate::face::preprocess::{self, REC_INPUT_SIZE};
use image::RgbImage;

/// Embedding dimension for ArcFace 512-d model.
pub const EMBEDDING_DIM: usize = 512;

pub struct FaceRecognizer {
    session: Session,
}

impl FaceRecognizer {
    /// Load the ArcFace recognizer model from the buffalo_l directory.
    pub fn new(model_dir: &Path) -> AppResult<Self> {
        // Try common model filenames
        let candidates = ["w600k_r50.onnx", "arcface_r100_v1.onnx", "w600k_mbf.onnx"];
        let model_path = candidates
            .iter()
            .map(|name| model_dir.join(name))
            .find(|p| p.exists())
            .ok_or_else(|| {
                AppError::other(format!(
                    "Recognizer model not found in {} (tried: {:?})",
                    model_dir.display(),
                    candidates
                ))
            })?;

        let session = Session::builder()?
            .with_optimization_level(GraphOptimizationLevel::Level3)?
            .with_intra_threads(4)?
            .commit_from_file(&model_path)?;

        Ok(Self { session })
    }

    /// Extract a 512-d embedding from an aligned 112x112 face image.
    /// The embedding is L2-normalized (matching insightface output).
    pub fn extract_embedding(&mut self, aligned_face: &RgbImage) -> AppResult<Vec<f32>> {
        // Preprocess: normalize, transpose to CHW
        let input_tensor = preprocess::preprocess_for_recognition(aligned_face);

        // Add batch dimension: (3, 112, 112) -> (1, 3, 112, 112)
        let input_4d = input_tensor.insert_axis(Axis(0));

        // Flatten to (data, shape) for ort (Array4 doesn't implement OwnedTensorArrayData)
        let shape = vec![
            1i64,
            3,
            REC_INPUT_SIZE as i64,
            REC_INPUT_SIZE as i64,
        ];
        let flat_data: Vec<f32> = input_4d.iter().cloned().collect();

        // Run inference (ort 2.0.0-rc.13 API)
        let input_value = Tensor::from_array((shape, flat_data))?;
        let outputs = self.session.run(ort::inputs![input_value])?;

        // Extract embedding from output (shape: 1x512 or 512)
        // ort 2.0.0-rc.13: try_extract_tensor returns (&Shape, &[f32])
        let (_shape, embedding_data) = outputs[0].try_extract_tensor::<f32>()?;
        let embedding: Vec<f32> = embedding_data.to_vec();

        // Ensure 512-d
        let mut emb = if embedding.len() >= EMBEDDING_DIM {
            embedding[..EMBEDDING_DIM].to_vec()
        } else {
            let mut v = embedding.clone();
            v.resize(EMBEDDING_DIM, 0.0);
            v
        };

        // L2 normalize (matching insightface normed_embedding)
        let norm: f32 = emb.iter().map(|x| x * x).sum::<f32>().sqrt();
        if norm > 1e-6 {
            for x in &mut emb {
                *x /= norm;
            }
        }

        Ok(emb)
    }
}
