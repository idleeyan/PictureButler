//! Face endpoints: list person faces / face thumbnail / SSE scan / SSE recluster / image faces / reassign.

use std::path::PathBuf;
use std::sync::Arc;

use axum::body::Body;
use axum::extract::{Path, Query, State};
use axum::http::{header, StatusCode};
use axum::response::{IntoResponse, Response, Sse};
use axum::Json;
use futures_util::stream::Stream;
use rusqlite::params;
use serde::Deserialize;
use serde_json::json;
use tokio_stream::wrappers::ReceiverStream;

use super::AppState;
use crate::db::persons as db;
use crate::error::AppError;
use crate::models::request::{ProcessRequest, ReassignFaceRequest};
use crate::utils::sse::ProgressEvent;

#[derive(Deserialize)]
pub struct ListPersonFacesParams {
    #[serde(default = "default_limit")]
    pub limit: i64,
}

fn default_limit() -> i64 { 100 }

/// GET /api/persons/{id}/faces
pub async fn list_person_faces(
    State(state): State<Arc<AppState>>,
    Path(person_id): Path<i64>,
    Query(params): Query<ListPersonFacesParams>,
) -> Result<Json<serde_json::Value>, AppError> {
    let faces = tokio::task::spawn_blocking({
        let pool = state.db.clone();
        move || db::list_person_faces(&pool, person_id, params.limit)
    })
    .await
    .map_err(|e| AppError::internal(e.to_string()))??;
    Ok(Json(json!(faces)))
}

/// GET /api/face-thumbnail/{face_id}
pub async fn face_thumbnail(
    State(state): State<Arc<AppState>>,
    Path(face_id): Path<i64>,
) -> Result<Response, AppError> {
    // Check cached thumbnail first
    let thumb_path = state.config.faces_dir.join(format!("{}.jpg", face_id));
    if thumb_path.exists() {
        let bytes = tokio::fs::read(&thumb_path)
            .await
            .map_err(|e| AppError::internal(format!("Failed to read thumbnail: {}", e)))?;
        return Ok((
            StatusCode::OK,
            [(header::CONTENT_TYPE, "image/jpeg")],
            Body::from(bytes),
        ).into_response());
    }

    // No cache — generate from original image
    let (image_id, bbox_str) = tokio::task::spawn_blocking({
        let pool = state.db.clone();
        move || db::get_face_info(&pool, face_id)
    })
    .await
    .map_err(|e| AppError::internal(e.to_string()))??;

    // Get image path
    let image_path = tokio::task::spawn_blocking({
        let pool = state.db.clone();
        move || -> Result<Option<String>, AppError> {
            let conn = crate::db::get_conn(&pool)?;
            let path: Option<String> = conn
                .query_row("SELECT path FROM images WHERE id=?", params![image_id], |r| r.get(0))
                .ok();
            Ok(path)
        }
    })
    .await
    .map_err(|e| AppError::internal(e.to_string()))??;

    let Some(image_path) = image_path else {
        return Err(AppError::not_found("Original image not found"));
    };

    // Parse bbox
    let bbox: Vec<i64> = bbox_str
        .split(',')
        .filter_map(|s| s.trim().parse::<i64>().ok())
        .collect();
    if bbox.len() != 4 {
        return Err(AppError::internal("Invalid bbox format"));
    }

    // Generate and save thumbnail
    let faces_dir = state.config.faces_dir.clone();
    let thumb_path_clone = thumb_path.clone();
    let result = tokio::task::spawn_blocking(move || -> Result<(), AppError> {
        std::fs::create_dir_all(&faces_dir)?;
        let img = crate::imageproc::io::decode_rgb(&PathBuf::from(&image_path))?;
        let (w, h) = (img.width(), img.height());
        let x1 = (bbox[0] as u32).max(0).min(w);
        let y1 = (bbox[1] as u32).max(0).min(h);
        let x2 = (bbox[2] as u32).max(0).min(w);
        let y2 = (bbox[3] as u32).max(0).min(h);
        if x2 <= x1 || y2 <= y1 {
            return Err(AppError::internal("Invalid bbox dimensions"));
        }
        // Add padding for avatar-like appearance
        let pad_x = ((x2 - x1) as f32 * 0.2) as u32;
        let pad_y = ((y2 - y1) as f32 * 0.3) as u32;
        let x1 = x1.saturating_sub(pad_x);
        let y1 = y1.saturating_sub(pad_y);
        let x2 = (x2 + pad_x).min(w);
        let y2 = (y2 + pad_y).min(h);

        let cropped = image::imageops::crop_imm(&img, x1, y1, x2 - x1, y2 - y1).to_image();
        let thumb = crate::imageproc::resize::thumbnail(
            &image::DynamicImage::from(cropped),
            200,
        );
        let bytes = crate::imageproc::io::encode_jpeg(&thumb, 85);
        std::fs::write(&thumb_path_clone, &bytes)?;
        Ok(())
    })
    .await
    .map_err(|e| AppError::internal(e.to_string()))?;

    match result {
        Ok(()) => {
            let bytes = tokio::fs::read(&thumb_path)
                .await
                .map_err(|e| AppError::internal(format!("Failed to read thumbnail: {}", e)))?;
            Ok((
                StatusCode::OK,
                [(header::CONTENT_TYPE, "image/jpeg")],
                Body::from(bytes),
            ).into_response())
        }
        Err(e) => Err(e),
    }
}

#[derive(Deserialize)]
pub struct ScanFacesParams {
    #[serde(default)]
    pub force: bool,
}

/// POST /api/faces/scan — SSE stream of face scan progress.
pub async fn scan_faces(
    State(state): State<Arc<AppState>>,
    Query(params): Query<ScanFacesParams>,
    Json(body): Json<ProcessRequest>,
) -> Result<Sse<impl Stream<Item = Result<axum::response::sse::Event, std::convert::Infallible>>>, AppError> {
    // Parse image_ids
    let image_ids: Vec<i64> = if body.image_ids.is_empty() {
        vec![]
    } else {
        body.image_ids
            .split(',')
            .filter_map(|s| s.trim().parse::<i64>().ok())
            .collect()
    };

    let folder_like = crate::config::resolve_folder_path(&body.folder);

    // Load images to scan (in blocking thread)
    let images = tokio::task::spawn_blocking({
        let pool = state.db.clone();
        move || crate::db::images::get_images_for_face_scan(&pool, &image_ids, folder_like.as_deref(), params.force)
    })
    .await
    .map_err(|e| AppError::internal(e.to_string()))??;

    let total = images.len() as i64;
    let (tx, rx) = tokio::sync::mpsc::channel::<Result<axum::response::sse::Event, std::convert::Infallible>>(16);

    // Send start event
    let start_evt = ProgressEvent {
        event_type: "start".into(),
        current: 0,
        total,
        filename: None,
        status: None,
        description: None,
        tags: None,
        faces: None,
        total_faces: None,
        image_id: None,
        error: None,
        new_persons: None,
        clusters: None,
        named_seeds: None,
        scanned: None,
        failed: None,
    };
    let _ = tx.send(Ok(axum::response::sse::Event::default()
        .data(serde_json::to_string(&start_evt).unwrap()))).await;

    // Spawn background task for face scanning
    let state_clone = state.clone();
    tokio::spawn(async move {
        let mut done = 0i64;
        let mut failed = 0i64;
        let mut total_faces = 0i64;

        for img in images {
            let path = PathBuf::from(&img.path);
            if !path.exists() {
                failed += 1;
                done += 1;
                let evt = ProgressEvent {
                    event_type: "progress".into(),
                    current: done,
                    total,
                    filename: Some(img.filename.clone()),
                    status: Some("skipped".into()),
                    error: Some("File not found".into()),
                    ..ProgressEvent::progress(done, total)
                };
                let _ = tx.send(Ok(axum::response::sse::Event::default()
                    .data(serde_json::to_string(&evt).unwrap()))).await;
                continue;
            }

            // Run face detection (blocking, CPU-intensive)
            let face_app = state_clone.face.clone();
            let img_path = path.clone();
            let img_id = img.id;
            let pool = state_clone.db.clone();
            let filename = img.filename.clone();
            let result = tokio::task::spawn_blocking(move || -> Result<i64, String> {
                let faces = face_app.detect_faces(&img_path).map_err(|e| e.to_string())?;
                if faces.is_empty() {
                    // 无检测结果：清除历史误检框（如旧版本写入的错误框），再标记已扫描
                    let conn = crate::db::get_conn(&pool).map_err(|e| e.to_string())?;
                    conn.execute("DELETE FROM image_faces WHERE image_id=?", params![img_id])
                        .map_err(|e| e.to_string())?;
                    conn.execute("UPDATE images SET faces_scanned=1 WHERE id=?", params![img_id])
                        .map_err(|e| e.to_string())?;
                    return Ok(0);
                }

                let conn = crate::db::get_conn(&pool).map_err(|e| e.to_string())?;
                // Delete old faces for this image
                conn.execute("DELETE FROM image_faces WHERE image_id=?", params![img_id])
                    .map_err(|e| e.to_string())?;

                let threshold = {
                    let s = face_app.config_ref().settings.read();
                    s.face_sim_thresh
                };

                let mut added = 0i64;
                for face in &faces {
                    // Match against existing persons
                    let pid = crate::db::persons::match_person(&conn, &face.embedding, threshold)
                        .map_err(|e| e.to_string())?;
                    let final_pid = match pid {
                        Some(p) => p,
                        None => crate::db::persons::create_person(&conn).map_err(|e| e.to_string())?,
                    };
                    let bbox_str = format!("{},{},{},{}", face.bbox[0], face.bbox[1], face.bbox[2], face.bbox[3]);
                    let face_id = crate::db::persons::insert_face(
                        &conn, img_id, final_pid, &bbox_str, &face.embedding, face.score as f64,
                    ).map_err(|e| e.to_string())?;
                    crate::db::persons::set_avatar_if_needed(&conn, final_pid, face_id)
                        .map_err(|e| e.to_string())?;
                    added += 1;
                }
                conn.execute("UPDATE images SET faces_scanned=1 WHERE id=?", params![img_id])
                    .map_err(|e| e.to_string())?;
                Ok(added)
            })
            .await;

            match result {
                Ok(Ok(n)) => {
                    total_faces += n;
                    done += 1;
                    let evt = ProgressEvent {
                        event_type: "progress".into(),
                        current: done,
                        total,
                        filename: Some(filename),
                        status: Some("done".into()),
                        faces: Some(n),
                        total_faces: Some(total_faces),
                        image_id: Some(img.id),
                        ..ProgressEvent::progress(done, total)
                    };
                    let _ = tx.send(Ok(axum::response::sse::Event::default()
                        .data(serde_json::to_string(&evt).unwrap()))).await;
                }
                Ok(Err(e)) => {
                    failed += 1;
                    done += 1;
                    let evt = ProgressEvent {
                        event_type: "progress".into(),
                        current: done,
                        total,
                        filename: Some(filename),
                        status: Some("failed".into()),
                        error: Some(e),
                        image_id: Some(img.id),
                        ..ProgressEvent::progress(done, total)
                    };
                    let _ = tx.send(Ok(axum::response::sse::Event::default()
                        .data(serde_json::to_string(&evt).unwrap()))).await;
                }
                Err(e) => {
                    failed += 1;
                    done += 1;
                    let evt = ProgressEvent {
                        event_type: "progress".into(),
                        current: done,
                        total,
                        filename: Some(filename),
                        status: Some("failed".into()),
                        error: Some(e.to_string()),
                        image_id: Some(img.id),
                        ..ProgressEvent::progress(done, total)
                    };
                    let _ = tx.send(Ok(axum::response::sse::Event::default()
                        .data(serde_json::to_string(&evt).unwrap()))).await;
                }
            }
        }

        // Done event
        let done_evt = ProgressEvent {
            event_type: "done".into(),
            current: done,
            total,
            scanned: Some(done),
            failed: Some(failed),
            total_faces: Some(total_faces),
            ..ProgressEvent::done(total)
        };
        let _ = tx.send(Ok(axum::response::sse::Event::default()
            .data(serde_json::to_string(&done_evt).unwrap()))).await;
    });

    let stream = ReceiverStream::new(rx);
    Ok(Sse::new(stream).keep_alive(axum::response::sse::KeepAlive::default()))
}

/// POST /api/faces/recluster — SSE stream of recluster progress.
pub async fn recluster_faces(
    State(state): State<Arc<AppState>>,
) -> Result<Sse<impl Stream<Item = Result<axum::response::sse::Event, std::convert::Infallible>>>, AppError> {
    let (tx, rx) = tokio::sync::mpsc::channel::<Result<axum::response::sse::Event, std::convert::Infallible>>(16);

    let state_clone = state.clone();
    let tx_clone = tx.clone();
    tokio::spawn(async move {
        let result = tokio::task::spawn_blocking(move || -> Result<(), String> {
            let pool = state_clone.db.clone();
            let threshold = {
                let s = state_clone.config.settings.read();
                s.face_sim_thresh
            };

            // Load all embeddings
            let embeddings = crate::db::persons::load_all_embeddings(&pool).map_err(|e| e.to_string())?;
            let total = embeddings.len() as i64;

            // Load named persons (seeds)
            let named = crate::db::persons::load_named_persons(&pool).map_err(|e| e.to_string())?;

            // Build seed embeddings using avatar_face_id
            let emb_map: std::collections::HashMap<i64, Vec<f32>> = embeddings.iter().cloned().collect();
            let mut named_seeds: Vec<(i64, String, Option<i64>, Vec<f32>)> = Vec::new();
            for (pid, name, av_id) in &named {
                if let Some(av) = av_id {
                    if let Some(emb) = emb_map.get(av) {
                        named_seeds.push((*pid, name.clone(), *av_id, emb.clone()));
                    }
                }
            }

            // Send start event
            let start_evt = ProgressEvent {
                event_type: "start".into(),
                current: 0,
                total,
                named_seeds: Some(named_seeds.len() as i64),
                ..ProgressEvent::start(total)
            };
            let _ = tx_clone.blocking_send(Ok(axum::response::sse::Event::default()
                .data(serde_json::to_string(&start_evt).unwrap())));

            // Recluster
            let assignments = crate::face::cluster::recluster(&embeddings, &named_seeds, threshold);

            // Apply: clear + reassign
            let conn = crate::db::get_conn(&pool).map_err(|e| e.to_string())?;
            let named_ids: Vec<i64> = named_seeds.iter().map(|(pid, _, _, _)| *pid).collect();
            crate::db::persons::delete_unnamed_persons(&conn, &named_ids).map_err(|e| e.to_string())?;
            crate::db::persons::clear_all_person_assignments(&conn).map_err(|e| e.to_string())?;

            let mut done = 0i64;
            let mut new_persons = 0i64;
            let clusters_count = named_seeds.len() as i64;

            for assign in &assignments {
                if assign.is_new {
                    // Create new person
                    let new_pid = crate::db::persons::create_person(&conn).map_err(|e| e.to_string())?;
                    crate::db::persons::update_face_person(&conn, assign.face_id, new_pid).map_err(|e| e.to_string())?;
                    crate::db::persons::set_person_avatar(&conn, new_pid, assign.face_id).map_err(|e| e.to_string())?;
                    new_persons += 1;
                } else {
                    crate::db::persons::update_face_person(&conn, assign.face_id, assign.person_id).map_err(|e| e.to_string())?;
                }
                done += 1;
                if done % 20 == 0 || done == total {
                    let evt = ProgressEvent {
                        event_type: "progress".into(),
                        current: done,
                        total,
                        new_persons: Some(new_persons),
                        clusters: Some(clusters_count + new_persons),
                        ..ProgressEvent::progress(done, total)
                    };
                    let _ = tx_clone.blocking_send(Ok(axum::response::sse::Event::default()
                        .data(serde_json::to_string(&evt).unwrap())));
                }
            }

            // Done event
            let done_evt = ProgressEvent {
                event_type: "done".into(),
                current: total,
                total,
                new_persons: Some(new_persons),
                clusters: Some(clusters_count + new_persons),
                named_seeds: Some(named_seeds.len() as i64),
                ..ProgressEvent::done(total)
            };
            let _ = tx_clone.blocking_send(Ok(axum::response::sse::Event::default()
                .data(serde_json::to_string(&done_evt).unwrap())));

            Ok(())
        })
        .await;

        if let Err(e) = result {
            let evt = ProgressEvent {
                event_type: "error".into(),
                error: Some(e.to_string()),
                ..ProgressEvent::done(0)
            };
            let _ = tx.send(Ok(axum::response::sse::Event::default()
                .data(serde_json::to_string(&evt).unwrap()))).await;
        }
    });

    let stream = ReceiverStream::new(rx);
    Ok(Sse::new(stream).keep_alive(axum::response::sse::KeepAlive::default()))
}

/// GET /api/images/{id}/faces
pub async fn get_image_faces(
    State(state): State<Arc<AppState>>,
    Path(image_id): Path<i64>,
) -> Result<Json<serde_json::Value>, AppError> {
    let faces = tokio::task::spawn_blocking({
        let pool = state.db.clone();
        move || db::get_image_faces(&pool, image_id)
    })
    .await
    .map_err(|e| AppError::internal(e.to_string()))??;
    Ok(Json(json!(faces)))
}

/// PATCH /api/faces/{face_id}/person
pub async fn reassign_face(
    State(state): State<Arc<AppState>>,
    Path(face_id): Path<i64>,
    Json(body): Json<ReassignFaceRequest>,
) -> Result<Json<serde_json::Value>, AppError> {
    let new_name = body.new_name.as_deref().map(|s| s.trim()).filter(|s| !s.is_empty());
    let pid = tokio::task::spawn_blocking({
        let pool = state.db.clone();
        let n = new_name.map(|s| s.to_string());
        move || db::reassign_face(&pool, face_id, body.person_id, n.as_deref())
    })
    .await
    .map_err(|e| AppError::internal(e.to_string()))??;
    Ok(Json(json!({"ok": true, "person_id": pid})))
}
