//! Process endpoints: scan / process (SSE) / search / rescan / batch delete/tag/move.

use std::path::PathBuf;
use std::sync::Arc;

use axum::extract::State;
use axum::response::Sse;
use axum::Json;
use futures_util::stream::Stream;
use rusqlite::params;
use serde_json::json;
use tokio_stream::wrappers::ReceiverStream;

use super::AppState;
use crate::error::AppError;
use crate::models::request::{
    BatchDeleteRequest, BatchMoveRequest, BatchTagRequest, FolderRequest, ProcessRequest,
    SearchRequest,
};
use crate::utils::sse::ProgressEvent;

/// POST /api/scan — scan a folder and add new images.
pub async fn scan_folder(
    State(state): State<Arc<AppState>>,
    Json(body): Json<FolderRequest>,
) -> Result<Json<serde_json::Value>, AppError> {
    let folder = PathBuf::from(body.folder.trim());
    if !folder.exists() {
        return Err(AppError::bad_request(format!("Folder does not exist: {}", body.folder)));
    }
    let folder_str = folder.to_string_lossy().to_string();
    let (added, total) = tokio::task::spawn_blocking({
        let pool = state.db.clone();
        move || crate::db::images::scan_folder(&pool, &folder)
    })
    .await
    .map_err(|e| AppError::internal(e.to_string()))??;

    Ok(Json(json!({
        "added": added,
        "total": total,
        "folder": folder_str,
    })))
}

/// POST /api/process — SSE stream of AI tagging progress.
pub async fn process_images(
    State(state): State<Arc<AppState>>,
    Json(body): Json<ProcessRequest>,
) -> Result<Sse<impl Stream<Item = Result<axum::response::sse::Event, std::convert::Infallible>>>, AppError> {
    let image_ids: Vec<i64> = if body.image_ids.is_empty() {
        vec![]
    } else {
        body.image_ids
            .split(',')
            .filter_map(|s| s.trim().parse::<i64>().ok())
            .collect()
    };
    let folder_like = crate::config::resolve_folder_path(&body.folder);
    let limit = body.limit;
    let force = body.force;

    let images = tokio::task::spawn_blocking({
        let pool = state.db.clone();
        move || crate::db::images::get_images_for_processing(&pool, &image_ids, folder_like.as_deref(), limit, force)
    })
    .await
    .map_err(|e| AppError::internal(e.to_string()))??;

    let total = images.len() as i64;
    let (tx, rx) = tokio::sync::mpsc::channel::<Result<axum::response::sse::Event, std::convert::Infallible>>(16);

    // Send start event
    let start_evt = ProgressEvent::start(total);
    let _ = tx.send(Ok(axum::response::sse::Event::default()
        .data(serde_json::to_string(&start_evt).unwrap()))).await;

    let state_clone = state.clone();
    tokio::spawn(async move {
        let mut done = 0i64;
        let mut failed = 0i64;

        for img in images {
            let filepath = PathBuf::from(&img.path);
            if !filepath.exists() {
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

            // Call LM Studio for AI tagging
            let lm_url = state_clone.config.settings.read().lm_studio_url.clone();
            let model = state_clone.config.settings.read().lm_studio_model.clone();
            let max_size = state_clone.config.max_image_size_tuple();
            let img_path = filepath.clone();
            let img_id = img.id;
            let filename = img.filename.clone();
            let pool = state_clone.db.clone();
            let face_app = state_clone.face.clone();

            let result = tokio::task::spawn_blocking(move || -> Result<(String, String, i64), String> {
                // Use blocking API with tokio runtime handle
                let rt = tokio::runtime::Handle::current();
                let (desc, tags) = rt.block_on(async {
                    crate::llm::analyze_image(&lm_url, &model, &img_path, max_size)
                        .await
                        .map_err(|e| e.to_string())
                })?;

                let conn = crate::db::get_conn(&pool).map_err(|e| e.to_string())?;
                conn.execute(
                    "UPDATE images SET description=?, tags=?, status='done' WHERE id=?",
                    params![desc, tags, img_id],
                ).map_err(|e| e.to_string())?;
                crate::db::tags::sync_image_tags(&conn, img_id, &tags).map_err(|e| e.to_string())?;

                // Also run face detection
                let face_count = match face_app.detect_faces(&img_path) {
                    Ok(faces) => {
                        if faces.is_empty() {
                            // 无检测结果：清除历史误检框，再标记已扫描
                            conn.execute("DELETE FROM image_faces WHERE image_id=?", params![img_id])
                                .map_err(|e| e.to_string())?;
                            conn.execute("UPDATE images SET faces_scanned=1 WHERE id=?", params![img_id])
                                .map_err(|e| e.to_string())?;
                            0
                        } else {
                            conn.execute("DELETE FROM image_faces WHERE image_id=?", params![img_id])
                                .map_err(|e| e.to_string())?;
                            let threshold = face_app.config_ref().settings.read().face_sim_thresh;
                            let mut added = 0i64;
                            for face in &faces {
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
                            added
                        }
                    }
                    Err(_) => 0,
                };

                Ok((desc, tags, face_count))
            })
            .await;

            match result {
                Ok(Ok((desc, tags, face_count))) => {
                    done += 1;
                    let evt = ProgressEvent {
                        event_type: "progress".into(),
                        current: done,
                        total,
                        filename: Some(filename),
                        status: Some("done".into()),
                        description: Some(desc),
                        tags: Some(tags),
                        faces: Some(face_count),
                        image_id: Some(img.id),
                        ..ProgressEvent::progress(done, total)
                    };
                    let _ = tx.send(Ok(axum::response::sse::Event::default()
                        .data(serde_json::to_string(&evt).unwrap()))).await;
                }
                Ok(Err(e)) => {
                    failed += 1;
                    done += 1;
                    // Mark as failed in DB
                    let pool = state_clone.db.clone();
                    let _ = tokio::task::spawn_blocking(move || -> Result<(), AppError> {
                        let conn = crate::db::get_conn(&pool)?;
                        conn.execute("UPDATE images SET status='failed' WHERE id=?", params![img.id])?;
                        Ok(())
                    }).await;
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

        let done_evt = ProgressEvent {
            event_type: "done".into(),
            current: done,
            total,
            scanned: Some(done),
            failed: Some(failed),
            ..ProgressEvent::done(total)
        };
        let _ = tx.send(Ok(axum::response::sse::Event::default()
            .data(serde_json::to_string(&done_evt).unwrap()))).await;
    });

    let stream = ReceiverStream::new(rx);
    Ok(Sse::new(stream).keep_alive(axum::response::sse::KeepAlive::default()))
}

/// POST /api/search — full-text search + tag filter.
pub async fn search_images(
    State(state): State<Arc<AppState>>,
    Json(body): Json<SearchRequest>,
) -> Result<Json<serde_json::Value>, AppError> {
    let folder_like = crate::config::resolve_folder_path(&body.folder);
    let per_page = body.per_page.clamp(1, 10000);
    let page = body.page.max(1);

    let resp = tokio::task::spawn_blocking({
        let pool = state.db.clone();
        let query = body.query.clone();
        let tags = body.tags.clone();
        let tag_mode = body.tag_mode.clone();
        move || crate::db::images::search_images(
            &pool, &query, folder_like.as_deref(), &tags, &tag_mode, page, per_page,
        )
    })
    .await
    .map_err(|e| AppError::internal(e.to_string()))??;

    Ok(Json(json!(resp)))
}

/// POST /api/rescan — rescan a folder (remove deleted, add new).
pub async fn rescan_folder(
    State(state): State<Arc<AppState>>,
    Json(body): Json<FolderRequest>,
) -> Result<Json<serde_json::Value>, AppError> {
    let folder = PathBuf::from(body.folder.trim());
    if !folder.exists() {
        return Err(AppError::bad_request(format!("Folder does not exist: {}", body.folder)));
    }
    let folder_str = folder.to_string_lossy().to_string();
    let (removed, added, total) = tokio::task::spawn_blocking({
        let pool = state.db.clone();
        move || crate::db::images::rescan_folder(&pool, &folder)
    })
    .await
    .map_err(|e| AppError::internal(e.to_string()))??;

    Ok(Json(json!({
        "removed": removed,
        "added": added,
        "total": total,
        "folder": folder_str,
    })))
}

/// POST /api/images/batch — batch delete images.
pub async fn batch_delete(
    State(state): State<Arc<AppState>>,
    Json(body): Json<BatchDeleteRequest>,
) -> Result<Json<serde_json::Value>, AppError> {
    if body.ids.is_empty() {
        return Err(AppError::bad_request("ids cannot be empty"));
    }
    if body.ids.len() > 5000 {
        return Err(AppError::bad_request("Cannot process more than 5000 images at once"));
    }

    let (deleted, db_failures, id_to_path) = tokio::task::spawn_blocking({
        let pool = state.db.clone();
        let ids = body.ids.clone();
        move || crate::db::images::batch_delete(&pool, &ids)
    })
    .await
    .map_err(|e| AppError::internal(e.to_string()))??;

    // Delete files if requested
    let mut file_failures: Vec<serde_json::Value> = Vec::new();
    if body.delete_file {
        for img_id in &body.ids {
            if let Some(path) = id_to_path.get(img_id) {
                let p = PathBuf::from(path);
                if p.exists() {
                    if let Err(e) = crate::utils::trash::send_to_trash(&p) {
                        file_failures.push(json!({"id": img_id, "path": path, "error": e.to_string()}));
                    }
                }
                // Clean thumbnail cache
                crate::imageproc::thumb_cache::clean_for_path(path, &state.config.thumbs_dir);
            }
        }
    }

    Ok(Json(json!({
        "ok": true,
        "total": body.ids.len(),
        "deleted": deleted,
        "file_deleted": body.delete_file,
        "db_failures": db_failures,
        "file_failures": file_failures,
    })))
}

/// POST /api/images/batch-tag — batch add/remove tag.
pub async fn batch_tag(
    State(state): State<Arc<AppState>>,
    Json(body): Json<BatchTagRequest>,
) -> Result<Json<serde_json::Value>, AppError> {
    if body.ids.is_empty() {
        return Err(AppError::bad_request("ids cannot be empty"));
    }
    let tag = body.tag.trim();
    if tag.is_empty() {
        return Err(AppError::bad_request("tag cannot be empty"));
    }

    let (updated, skipped) = tokio::task::spawn_blocking({
        let pool = state.db.clone();
        let ids = body.ids.clone();
        let tag = tag.to_string();
        let mode = body.mode.clone();
        move || crate::db::images::batch_tag(&pool, &ids, &tag, &mode)
    })
    .await
    .map_err(|e| AppError::internal(e.to_string()))??;

    Ok(Json(json!({
        "ok": true,
        "total": body.ids.len(),
        "updated": updated,
        "skipped": skipped,
    })))
}

/// POST /api/images/batch-move — batch move images to a new directory.
pub async fn batch_move(
    State(state): State<Arc<AppState>>,
    Json(body): Json<BatchMoveRequest>,
) -> Result<Json<serde_json::Value>, AppError> {
    if body.ids.is_empty() {
        return Err(AppError::bad_request("ids cannot be empty"));
    }
    if body.target_dir.trim().is_empty() {
        return Err(AppError::bad_request("target_dir cannot be empty"));
    }
    if body.ids.len() > 5000 {
        return Err(AppError::bad_request("Cannot process more than 5000 images at once"));
    }

    let target_path = PathBuf::from(body.target_dir.trim());
    // Create target directory
    if let Err(e) = tokio::fs::create_dir_all(&target_path).await {
        return Err(AppError::bad_request(format!("Failed to create target directory: {}", e)));
    }

    // Get image paths
    let id_to_path = tokio::task::spawn_blocking({
        let pool = state.db.clone();
        let ids = body.ids.clone();
        move || -> Result<std::collections::HashMap<i64, String>, AppError> {
            let conn = crate::db::get_conn(&pool)?;
            let placeholders: Vec<String> = ids.iter().map(|_| "?".to_string()).collect();
            let sql = format!("SELECT id, path FROM images WHERE id IN ({})", placeholders.join(","));
            let mut stmt = conn.prepare(&sql)?;
            let params_refs: Vec<&dyn rusqlite::ToSql> = ids.iter().map(|id| id as &dyn rusqlite::ToSql).collect();
            let iter = stmt.query_map(params_refs.as_slice(), |r| Ok((r.get::<_, i64>(0)?, r.get::<_, String>(1)?)))?;
            Ok(iter.filter_map(|r| r.ok()).collect())
        }
    })
    .await
    .map_err(|e| AppError::internal(e.to_string()))??;

    let mut moved = 0i64;
    let mut skipped = 0i64;
    let mut failures: Vec<serde_json::Value> = Vec::new();

    for img_id in &body.ids {
        let old_path_str = match id_to_path.get(img_id) {
            Some(p) => p.clone(),
            None => {
                failures.push(json!({"id": img_id, "error": "Image not found"}));
                continue;
            }
        };

        let src = PathBuf::from(&old_path_str);
        if !src.exists() {
            failures.push(json!({"id": img_id, "error": "Source file not found", "path": old_path_str}));
            continue;
        }

        // Compute target filename (handle name collisions)
        let mut dst = target_path.join(src.file_name().unwrap_or_default());
        if dst.exists() {
            // Check if same file
            let same = src.canonicalize().ok() == dst.canonicalize().ok();
            if same {
                skipped += 1;
                continue;
            }
            // Add _1, _2, ...
            let stem = src.file_stem().and_then(|s| s.to_str()).unwrap_or("image").to_string();
            let ext = src.extension().and_then(|s| s.to_str()).unwrap_or("").to_string();
            let mut i = 1;
            loop {
                let candidate = if ext.is_empty() {
                    target_path.join(format!("{}_{}", stem, i))
                } else {
                    target_path.join(format!("{}_{}.{}", stem, i, ext))
                };
                if !candidate.exists() {
                    dst = candidate;
                    break;
                }
                i += 1;
            }
        }

        // Copy + unlink (avoid OneDrive rename issues, like Python version)
        match tokio::fs::copy(&src, &dst).await {
            Ok(_) => {
                // Delete source (failure doesn't affect main flow)
                let _ = tokio::fs::remove_file(&src).await;
                let new_path = dst.to_string_lossy().to_string();
                let new_filename = dst.file_name().and_then(|s| s.to_str()).unwrap_or("").to_string();

                // Update DB
                let pool = state.db.clone();
                let img_id_val = *img_id;
                let new_path_clone = new_path.clone();
                let new_filename_clone = new_filename.clone();
                let db_result = tokio::task::spawn_blocking(move || -> Result<(), AppError> {
                    let conn = crate::db::get_conn(&pool)?;
                    conn.execute(
                        "UPDATE images SET path=?, filename=? WHERE id=?",
                        params![new_path_clone, new_filename_clone, img_id_val],
                    )?;
                    Ok(())
                })
                .await;

                match db_result {
                    Ok(Ok(())) => {
                        moved += 1;
                        // Clean old thumbnail cache
                        crate::imageproc::thumb_cache::clean_for_path(&old_path_str, &state.config.thumbs_dir);
                    }
                    Ok(Err(e)) => {
                        failures.push(json!({"id": img_id, "error": e.to_string(), "path": old_path_str}));
                    }
                    Err(e) => {
                        failures.push(json!({"id": img_id, "error": e.to_string(), "path": old_path_str}));
                    }
                }
            }
            Err(e) => {
                failures.push(json!({"id": img_id, "error": e.to_string(), "path": old_path_str}));
            }
        }
    }

    Ok(Json(json!({
        "ok": true,
        "total": body.ids.len(),
        "moved": moved,
        "skipped": skipped,
        "failures": failures,
        "target_dir": body.target_dir,
    })))
}
