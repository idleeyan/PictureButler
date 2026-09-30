//! Face clustering: greedy matching + recluster (replaces recluster_faces in main.py).
//!
//! Uses cosine similarity to group face embeddings into person clusters.

use crate::db::persons;

/// A cluster of face embeddings belonging to the same person.
pub struct Cluster {
    pub person_id: i64,
    pub embeddings: Vec<Vec<f32>>,
}

/// Recluster all face embeddings with a new threshold.
///
/// Algorithm (mirrors recluster_faces in main.py):
/// 1. Load all embeddings from DB
/// 2. Keep named persons (name != "人物N" or ignored=1) as seeds
/// 3. For each face, find the best-matching cluster
/// 4. If similarity >= threshold, assign to that cluster
/// 5. Otherwise, create a new cluster
///
/// Returns a list of (face_id, person_id) assignments.
pub fn recluster(
    embeddings: &[(i64, Vec<f32>)],  // (face_id, embedding)
    named_seeds: &[(i64, String, Option<i64>, Vec<f32>)],  // (person_id, name, avatar_face_id, seed_embedding)
    threshold: f64,
) -> Vec<ReclusterAssignment> {
    let mut clusters: Vec<Cluster> = named_seeds
        .iter()
        .map(|(pid, _, _, emb)| Cluster {
            person_id: *pid,
            embeddings: vec![emb.clone()],
        })
        .collect();

    let mut assignments = Vec::new();

    for (face_id, emb) in embeddings {
        // Find best matching cluster
        let mut best_pid: Option<i64> = None;
        let mut best_sim = 0.0f64;

        for cluster in &clusters {
            for cemb in &cluster.embeddings {
                let sim = persons::cosine_sim(emb, cemb);
                if sim > best_sim {
                    best_sim = sim;
                    best_pid = Some(cluster.person_id);
                }
            }
        }

        if best_sim >= threshold && best_pid.is_some() {
            // Assign to existing cluster
            let pid = best_pid.unwrap();
            if let Some(cluster) = clusters.iter_mut().find(|c| c.person_id == pid) {
                cluster.embeddings.push(emb.clone());
            }
            assignments.push(ReclusterAssignment {
                face_id: *face_id,
                person_id: pid,
                is_new: false,
            });
        } else {
            // This will be a new person — but we can't create it here (no DB access)
            // The caller (API handler) will create the person and assign the face
            assignments.push(ReclusterAssignment {
                face_id: *face_id,
                person_id: 0, // 0 = new person (caller creates)
                is_new: true,
            });
        }
    }

    assignments
}

/// A reclustering assignment result.
#[derive(Debug, Clone)]
pub struct ReclusterAssignment {
    pub face_id: i64,
    pub person_id: i64,
    pub is_new: bool,
}
