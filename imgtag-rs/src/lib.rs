//! imgtag_native —— imgtag 识别能力的**进程内** C ABI 库。
//!
//! 供 PictureButler（C#，WPF）通过 P/Invoke 直接调用，取代原先「独立 imgtag.exe +
//! HTTP 127.0.0.1:8520」的跨进程形态。融合后宿主机进程内即完成：
//!   · 人脸检测 / 特征提取 / 人物聚类（ONNX，CPU）
//!   · AI 打标（Rust 侧直连 LM Studio 127.0.0.1:1234）
//!   · 人脸缩略图裁剪
//!
//! 设计要点：
//!   1. **不复制业务逻辑**——内部直接复用 `face` / `db` / `llm` / `imageproc` 模块的既有实现；
//!   2. **数据复用**——打开的就是原来那份 `db.sqlite`，旧人脸/标签数据天然兼容；
//!   3. **数据交换用 JSON 字符串**——避免复杂结构体 FFI，两语言解耦；
//!   4. 全局单例 `Core`，`init` / `shutdown` 生命周期跟随宿主程序；
//!   5. 所有导出函数均 `catch_unwind` 包裹，panic 不会穿越 FFI 边界。

pub mod api;
pub mod config;
pub mod db;
pub mod error;
pub mod face;
pub mod imageproc;
pub mod llm;
pub mod models;
pub mod utils;

use std::ffi::{c_char, c_void, CStr, CString};
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex, MutexGuard, OnceLock};

use rusqlite::params;
use serde_json::json;

use crate::config::{info, warn, AppConfig};
use crate::db::DbPool;
use crate::face::FaceApp;
use crate::utils::sse::ProgressEvent;

/// 进度回调函数指针：宿主提供，Rust 每产生一个事件即回调一次。
///
/// `event_json` 是 UTF-8 的 JSON 字符串，字段与旧 SSE 的 `data:` 行**同构**
/// （含 `"type"`、`current`、`total`、`filename`、`status`、`total_faces`、
/// `scanned`、`failed`、`new_persons`、`error` 等），因此 C# 侧解析代码可直接沿用。
pub type ProgressFn = extern "C" fn(ctx: *mut c_void, event_json: *const c_char);

/// 进程内识别核心：一次性持有配置、连接池、人脸引擎与 tokio 运行时。
struct Core {
    config: AppConfig,
    pool: DbPool,
    face: Arc<FaceApp>,
    rt: tokio::runtime::Runtime,
    cancel: AtomicBool,
}

static CORE: OnceLock<Mutex<Option<Arc<Core>>>> = OnceLock::new();

fn core_slot() -> &'static Mutex<Option<Arc<Core>>> {
    CORE.get_or_init(|| Mutex::new(None))
}

/// 取锁（忽略中毒，避免一次 panic 永久锁死识别能力）
fn core_guard() -> MutexGuard<'static, Option<Arc<Core>>> {
    core_slot().lock().unwrap_or_else(|e| e.into_inner())
}

fn get_core() -> Option<Arc<Core>> {
    core_guard().as_ref().map(Arc::clone)
}

// ---------------------------------------------------------------- FFI 工具

/// 读取宿主传入的 UTF-8 C 字符串（空指针按空串处理）。
unsafe fn cstr_to_string(p: *const c_char) -> String {
    if p.is_null() {
        return String::new();
    }
    CStr::from_ptr(p).to_string_lossy().into_owned()
}

/// 把 Rust 字符串转成宿主负责释放的堆字符串。
fn to_c_string(s: String) -> *mut c_char {
    CString::new(s)
        .unwrap_or_else(|_| {
            CString::new(r#"{"ok":false,"error":"internal string contained NUL"}"#).unwrap()
        })
        .into_raw()
}

fn ok_json(v: serde_json::Value) -> *mut c_char {
    to_c_string(v.to_string())
}

fn err_json(msg: impl Into<String>) -> *mut c_char {
    to_c_string(json!({"ok": false, "error": msg.into()}).to_string())
}

unsafe fn write_err(err_out: *mut *mut c_char, msg: impl Into<String>) {
    if !err_out.is_null() {
        *err_out = to_c_string(msg.into());
    }
}

/// 统一出口：把 `Result` 转成 JSON 字符串指针，并处理 panic。
fn finish(r: Result<serde_json::Value, String>) -> *mut c_char {
    match r {
        Ok(v) => ok_json(v),
        Err(e) => err_json(e),
    }
}

/// 执行体包裹：panic → 错误 JSON，绝不让 unwind 穿越 FFI 边界。
fn guarded<F>(f: F) -> *mut c_char
where
    F: FnOnce() -> Result<serde_json::Value, String>,
{
    match catch_unwind(AssertUnwindSafe(f)) {
        Ok(r) => finish(r),
        Err(_) => err_json("识别引擎内部 panic（已拦截）"),
    }
}

#[derive(Clone, Copy)]
struct Emitter {
    cb: Option<ProgressFn>,
    ctx: *mut c_void,
}

// 回调指针由宿主保证在其生命周期内有效；仅用于跨线程回调事件。
unsafe impl Send for Emitter {}

impl Emitter {
    fn emit(&self, evt: &ProgressEvent) {
        let Some(cb) = self.cb else { return };
        let Ok(s) = serde_json::to_string(evt) else { return };
        let Ok(c) = CString::new(s) else { return };
        cb(self.ctx, c.as_ptr());
    }
}

// ---------------------------------------------------------------- 生命周期

/// 初始化识别引擎（幂等：已初始化则直接返回成功）。
///
/// * `base_dir`   —— 数据目录（应含 `models/`、`.faces/`、`.thumbs/`、`onnxruntime.dll`）
/// * `db_path`    —— `db.sqlite` 路径（空则取 `base_dir/db.sqlite`）
/// * `ort_dylib`  —— `onnxruntime.dll` 路径（空则自动探测 `base_dir/onnxruntime.dll`）
/// * `lm_url` / `lm_model` —— LM Studio 地址与视觉模型名（空则沿用库内已存设置）
///
/// 返回 0 表示成功；非 0 表示失败，此时 `err_out` 指向错误信息（需 `pb_imgtag_free_string` 释放）。
#[no_mangle]
pub extern "C" fn pb_imgtag_init(
    base_dir: *const c_char,
    db_path: *const c_char,
    ort_dylib: *const c_char,
    lm_url: *const c_char,
    lm_model: *const c_char,
    err_out: *mut *mut c_char,
) -> i32 {
    let result = catch_unwind(AssertUnwindSafe(|| {
        let base_dir = unsafe { cstr_to_string(base_dir) };
        let db_path = unsafe { cstr_to_string(db_path) };
        let ort_dylib = unsafe { cstr_to_string(ort_dylib) };
        let lm_url = unsafe { cstr_to_string(lm_url) };
        let lm_model = unsafe { cstr_to_string(lm_model) };
        do_init(base_dir, db_path, ort_dylib, lm_url, lm_model)
    }));

    match result {
        Ok(Ok(())) => 0,
        Ok(Err(msg)) => {
            unsafe { write_err(err_out, msg) };
            1
        }
        Err(_) => {
            unsafe { write_err(err_out, "初始化时发生内部 panic（已拦截）") };
            2
        }
    }
}

fn do_init(
    base_dir: String,
    db_path: String,
    ort_dylib: String,
    lm_url: String,
    lm_model: String,
) -> Result<(), String> {
    // 幂等：已初始化直接返回
    if get_core().is_some() {
        return Ok(());
    }

    let base = if base_dir.trim().is_empty() {
        std::env::current_exe()
            .ok()
            .and_then(|p| p.parent().map(PathBuf::from))
            .unwrap_or_else(|| PathBuf::from("."))
    } else {
        PathBuf::from(&base_dir)
    };

    // ort 以 load-dynamic 方式运行时加载 onnxruntime.dll，必须在首次推理前确定路径
    if !ort_dylib.trim().is_empty() && Path::new(&ort_dylib).exists() {
        std::env::set_var("ORT_DYLIB_PATH", &ort_dylib);
    } else if std::env::var("ORT_DYLIB_PATH").is_err() {
        let fallback = base.join("onnxruntime.dll");
        if fallback.exists() {
            std::env::set_var("ORT_DYLIB_PATH", &fallback);
        } else {
            warn(&format!("未找到 onnxruntime.dll（{}），人脸识别将不可用", fallback.display()));
        }
    }

    let mut config = AppConfig::new(base.clone());
    if !db_path.trim().is_empty() {
        config.db_path = PathBuf::from(&db_path);
    }

    std::fs::create_dir_all(&config.faces_dir).map_err(|e| format!("创建 .faces 失败: {e}"))?;
    std::fs::create_dir_all(&config.thumbs_dir).map_err(|e| format!("创建 .thumbs 失败: {e}"))?;
    std::fs::create_dir_all(&config.cache_dir).map_err(|e| format!("创建 .cache 失败: {e}"))?;

    let pool = db::init_pool(&config.db_path).map_err(|e| format!("打开识别库失败: {e}"))?;
    db::init_db(&pool).map_err(|e| format!("初始化识别库失败: {e}"))?;
    db::load_settings_into_config(&pool, &config).map_err(|e| format!("读取识别设置失败: {e}"))?;

    // 宿主传入的 LM Studio 参数优先（覆盖库内设置）
    {
        let mut s = config.settings.write();
        if !lm_url.trim().is_empty() {
            s.lm_studio_url = lm_url.clone();
        }
        if !lm_model.trim().is_empty() {
            s.lm_studio_model = lm_model.clone();
        }
    }

    let rt = tokio::runtime::Builder::new_multi_thread()
        .worker_threads(2)
        .enable_all()
        .build()
        .map_err(|e| format!("创建异步运行时失败: {e}"))?;

    let face = Arc::new(FaceApp::new(&config));

    // 后台回填缺失的图片元数据（宽高/大小/拍摄时间），与旧进程行为保持一致
    {
        let pool_bg = pool.clone();
        rt.spawn(async move { db::spawn_backfill_task(pool_bg) });
    }

    info(&format!(
        "imgtag_native 已初始化，base_dir = {}，db = {}",
        base.display(),
        config.db_path.display()
    ));

    let core = Arc::new(Core {
        config,
        pool,
        face,
        rt,
        cancel: AtomicBool::new(false),
    });
    *core_guard() = Some(core);
    Ok(())
}

/// 释放识别引擎：停用连接池与运行时。宿主退出前调用。
#[no_mangle]
pub extern "C" fn pb_imgtag_shutdown() -> i32 {
    let _ = catch_unwind(AssertUnwindSafe(|| {
        if let Some(core) = core_guard().take() {
            info("imgtag_native 关闭中…");
            core.cancel.store(true, Ordering::SeqCst);
            // Arc 引用计数归零时释放；若仍有识别线程持有引用，由其自然释放，无悬垂风险
            drop(core);
            info("imgtag_native 已关闭");
        }
    }));
    0
}

/// 引擎是否已就绪（1 = 就绪）。
#[no_mangle]
pub extern "C" fn pb_imgtag_health() -> i32 {
    if get_core().is_some() {
        1
    } else {
        0
    }
}

/// 请求取消当前识别任务（在图片之间检查，1 = 已置位）。
#[no_mangle]
pub extern "C" fn pb_imgtag_cancel() -> i32 {
    let Some(core) = get_core() else { return 0 };
    core.cancel.store(true, Ordering::SeqCst);
    1
}

/// 释放 Rust 侧返回的字符串。
#[no_mangle]
pub extern "C" fn pb_imgtag_free_string(s: *mut c_char) {
    if s.is_null() {
        return;
    }
    unsafe {
        drop(CString::from_raw(s));
    }
}

// ---------------------------------------------------------------- 人脸识别

/// 批量人脸识别（等价于旧 `POST /api/faces/scan`）。
///
/// `request_json`：`{"folder":"","image_ids":"1,2,3","force":false}`
/// 返回值 JSON：`{"ok":true,"scanned":n,"failed":n,"total_faces":n,"requested":n}`
#[no_mangle]
pub extern "C" fn pb_imgtag_scan_faces(
    request_json: *const c_char,
    ctx: *mut c_void,
    progress: Option<ProgressFn>,
) -> *mut c_char {
    let req_json = unsafe { cstr_to_string(request_json) };
    let em = Emitter { cb: progress, ctx };
    guarded(move || {
        let core = get_core().ok_or_else(|| "识别引擎未初始化".to_string())?;
        core.cancel.store(false, Ordering::SeqCst);
        do_scan_faces(&core, &req_json, &em)
    })
}

#[derive(serde::Deserialize)]
struct NativeRequest {
    #[serde(default)]
    folder: String,
    #[serde(default)]
    image_ids: String,
    #[serde(default)]
    force: bool,
    #[serde(default)]
    limit: i64,
}

fn do_scan_faces(
    core: &Core,
    req_json: &str,
    em: &Emitter,
) -> Result<serde_json::Value, String> {
    let req: NativeRequest =
        serde_json::from_str(req_json).map_err(|e| format!("请求 JSON 解析失败: {e}"))?;

    let image_ids = parse_ids(&req.image_ids);
    let folder_like = crate::config::resolve_folder_path(&req.folder);

    let images = crate::db::images::get_images_for_face_scan(
        &core.pool,
        &image_ids,
        folder_like.as_deref(),
        req.force,
    )
    .map_err(|e| e.to_string())?;

    let total = images.len() as i64;
    em.emit(&ProgressEvent::start(total));

    let mut done = 0i64;
    let mut failed = 0i64;
    let mut total_faces = 0i64;

    for img in images {
        if core.cancel.load(Ordering::SeqCst) {
            break;
        }
        let path = PathBuf::from(&img.path);
        if !path.exists() {
            failed += 1;
            done += 1;
            em.emit(&ProgressEvent {
                event_type: "progress".into(),
                current: done,
                total,
                filename: Some(img.filename.clone()),
                status: Some("skipped".into()),
                error: Some("File not found".into()),
                ..ProgressEvent::progress(done, total)
            });
            continue;
        }

        match scan_one_image(core, img.id, &path) {
            Ok(n) => {
                total_faces += n;
                done += 1;
                em.emit(&ProgressEvent {
                    event_type: "progress".into(),
                    current: done,
                    total,
                    filename: Some(img.filename.clone()),
                    status: Some("done".into()),
                    faces: Some(n),
                    total_faces: Some(total_faces),
                    image_id: Some(img.id),
                    ..ProgressEvent::progress(done, total)
                });
            }
            Err(e) => {
                failed += 1;
                done += 1;
                em.emit(&ProgressEvent {
                    event_type: "progress".into(),
                    current: done,
                    total,
                    filename: Some(img.filename.clone()),
                    status: Some("failed".into()),
                    error: Some(e),
                    image_id: Some(img.id),
                    ..ProgressEvent::progress(done, total)
                });
            }
        }
    }

    em.emit(&ProgressEvent {
        event_type: "done".into(),
        current: done,
        total,
        scanned: Some(done),
        failed: Some(failed),
        total_faces: Some(total_faces),
        ..ProgressEvent::done(total)
    });

    Ok(json!({
        "ok": true,
        "scanned": done,
        "failed": failed,
        "total_faces": total_faces,
        "requested": total,
    }))
}

/// 单张图片的人脸检测 + 聚类入库（逻辑与旧 `api/faces.rs` 完全一致）。
fn scan_one_image(core: &Core, img_id: i64, path: &Path) -> Result<i64, String> {
    let faces = core.face.detect_faces(path).map_err(|e| e.to_string())?;
    let conn = crate::db::get_conn(&core.pool).map_err(|e| e.to_string())?;

    // 先清除该图历史人脸框（含旧版本误检）
    conn.execute("DELETE FROM image_faces WHERE image_id=?", params![img_id])
        .map_err(|e| e.to_string())?;

    if faces.is_empty() {
        conn.execute("UPDATE images SET faces_scanned=1 WHERE id=?", params![img_id])
            .map_err(|e| e.to_string())?;
        return Ok(0);
    }

    let threshold = { core.face.config_ref().settings.read().face_sim_thresh };
    let mut added = 0i64;
    for face in &faces {
        let pid = crate::db::persons::match_person(&conn, &face.embedding, threshold)
            .map_err(|e| e.to_string())?;
        let final_pid = match pid {
            Some(p) => p,
            None => crate::db::persons::create_person(&conn).map_err(|e| e.to_string())?,
        };
        let bbox_str = format!(
            "{},{},{},{}",
            face.bbox[0], face.bbox[1], face.bbox[2], face.bbox[3]
        );
        let face_id = crate::db::persons::insert_face(
            &conn,
            img_id,
            final_pid,
            &bbox_str,
            &face.embedding,
            face.score as f64,
        )
        .map_err(|e| e.to_string())?;
        crate::db::persons::set_avatar_if_needed(&conn, final_pid, face_id)
            .map_err(|e| e.to_string())?;
        added += 1;
    }

    conn.execute("UPDATE images SET faces_scanned=1 WHERE id=?", params![img_id])
        .map_err(|e| e.to_string())?;
    Ok(added)
}

// ---------------------------------------------------------------- AI 打标

/// 批量 AI 打标（等价于旧 `POST /api/process`）。
///
/// `request_json`：`{"folder":"","image_ids":"1,2,3","limit":200}`
/// 返回值 JSON：`{"ok":true,"scanned":n,"failed":n}`
///
/// 说明：LM Studio 是**外部软件**，无法进程内合并；此处由 Rust 侧直连
/// `127.0.0.1:1234`（不再经「宿主 → 8520 → LM Studio」两级中转）。
#[no_mangle]
pub extern "C" fn pb_imgtag_ai_tag(
    request_json: *const c_char,
    ctx: *mut c_void,
    progress: Option<ProgressFn>,
) -> *mut c_char {
    let req_json = unsafe { cstr_to_string(request_json) };
    let em = Emitter { cb: progress, ctx };
    guarded(move || {
        let core = get_core().ok_or_else(|| "识别引擎未初始化".to_string())?;
        core.cancel.store(false, Ordering::SeqCst);
        do_ai_tag(&core, &req_json, &em)
    })
}

fn do_ai_tag(core: &Core, req_json: &str, em: &Emitter) -> Result<serde_json::Value, String> {
    let req: NativeRequest =
        serde_json::from_str(req_json).map_err(|e| format!("请求 JSON 解析失败: {e}"))?;

    let image_ids = parse_ids(&req.image_ids);
    let folder_like = crate::config::resolve_folder_path(&req.folder);

    let images =
        crate::db::images::get_images_for_processing(&core.pool, &image_ids, folder_like.as_deref(), req.limit, req.force)
            .map_err(|e| e.to_string())?;

    let total = images.len() as i64;
    em.emit(&ProgressEvent::start(total));

    let mut done = 0i64;
    let mut failed = 0i64;

    for img in images {
        if core.cancel.load(Ordering::SeqCst) {
            break;
        }
        let path = PathBuf::from(&img.path);
        if !path.exists() {
            failed += 1;
            done += 1;
            em.emit(&ProgressEvent {
                event_type: "progress".into(),
                current: done,
                total,
                filename: Some(img.filename.clone()),
                status: Some("skipped".into()),
                error: Some("File not found".into()),
                ..ProgressEvent::progress(done, total)
            });
            continue;
        }

        let (lm_url, model) = {
            let s = core.config.settings.read();
            (s.lm_studio_url.clone(), s.lm_studio_model.clone())
        };
        let max_size = core.config.max_image_size_tuple();

        // 外部 HTTP（LM Studio）：在本线程阻塞等待，宿主已用后台线程调用本函数
        let analyzed = core
            .rt
            .block_on(crate::llm::analyze_image(&lm_url, &model, &path, max_size))
            .map_err(|e| e.to_string());

        match analyzed {
            Ok((desc, tags)) => {
                let face_count = persist_tagged(core, img.id, &path, &desc, &tags)?;
                done += 1;
                em.emit(&ProgressEvent {
                    event_type: "progress".into(),
                    current: done,
                    total,
                    filename: Some(img.filename.clone()),
                    status: Some("done".into()),
                    description: Some(desc),
                    tags: Some(tags),
                    faces: Some(face_count),
                    image_id: Some(img.id),
                    ..ProgressEvent::progress(done, total)
                });
            }
            Err(e) => {
                failed += 1;
                done += 1;
                if let Ok(conn) = crate::db::get_conn(&core.pool) {
                    let _ = conn.execute(
                        "UPDATE images SET status='failed' WHERE id=?",
                        params![img.id],
                    );
                }
                em.emit(&ProgressEvent {
                    event_type: "progress".into(),
                    current: done,
                    total,
                    filename: Some(img.filename.clone()),
                    status: Some("failed".into()),
                    error: Some(e),
                    image_id: Some(img.id),
                    ..ProgressEvent::progress(done, total)
                });
            }
        }
    }

    em.emit(&ProgressEvent {
        event_type: "done".into(),
        current: done,
        total,
        scanned: Some(done),
        failed: Some(failed),
        ..ProgressEvent::done(total)
    });

    Ok(json!({"ok": true, "scanned": done, "failed": failed, "requested": total}))
}

/// 写回打标结果 + 顺带做人脸检测（与旧 `api/process.rs` 行为一致）。
fn persist_tagged(
    core: &Core,
    img_id: i64,
    path: &Path,
    desc: &str,
    tags: &str,
) -> Result<i64, String> {
    let conn = crate::db::get_conn(&core.pool).map_err(|e| e.to_string())?;
    conn.execute(
        "UPDATE images SET description=?, tags=?, status='done' WHERE id=?",
        params![desc, tags, img_id],
    )
    .map_err(|e| e.to_string())?;
    crate::db::tags::sync_image_tags(&conn, img_id, tags).map_err(|e| e.to_string())?;

    // 顺带人脸检测：失败不影响打标结果
    let Ok(faces) = core.face.detect_faces(path) else {
        return Ok(0);
    };

    conn.execute("DELETE FROM image_faces WHERE image_id=?", params![img_id])
        .map_err(|e| e.to_string())?;

    if faces.is_empty() {
        conn.execute("UPDATE images SET faces_scanned=1 WHERE id=?", params![img_id])
            .map_err(|e| e.to_string())?;
        return Ok(0);
    }

    let threshold = { core.face.config_ref().settings.read().face_sim_thresh };
    let mut added = 0i64;
    for face in &faces {
        let pid = crate::db::persons::match_person(&conn, &face.embedding, threshold)
            .map_err(|e| e.to_string())?;
        let final_pid = match pid {
            Some(p) => p,
            None => crate::db::persons::create_person(&conn).map_err(|e| e.to_string())?,
        };
        let bbox_str = format!(
            "{},{},{},{}",
            face.bbox[0], face.bbox[1], face.bbox[2], face.bbox[3]
        );
        let face_id = crate::db::persons::insert_face(
            &conn,
            img_id,
            final_pid,
            &bbox_str,
            &face.embedding,
            face.score as f64,
        )
        .map_err(|e| e.to_string())?;
        crate::db::persons::set_avatar_if_needed(&conn, final_pid, face_id)
            .map_err(|e| e.to_string())?;
        added += 1;
    }
    conn.execute("UPDATE images SET faces_scanned=1 WHERE id=?", params![img_id])
        .map_err(|e| e.to_string())?;
    Ok(added)
}

// ---------------------------------------------------------------- 人脸缩略图

/// 确保某张人脸的裁剪缩略图存在（等价于旧 `GET /api/face-thumbnail/{id}`）。
///
/// 已缓存则直接返回；否则从原图裁剪、缩放、写回 `.faces/{face_id}.jpg`。
/// 返回值 JSON：`{"ok":true,"path":"...\\.faces\\12.jpg"}`
#[no_mangle]
pub extern "C" fn pb_imgtag_ensure_face_thumbnail(face_id: i64) -> *mut c_char {
    guarded(move || {
        let core = get_core().ok_or_else(|| "识别引擎未初始化".to_string())?;
        let thumb_path = core.config.faces_dir.join(format!("{}.jpg", face_id));
        if !thumb_path.exists() {
            generate_face_thumbnail(&core, face_id, &thumb_path)?;
        }
        Ok(json!({
            "ok": true,
            "face_id": face_id,
            "path": thumb_path.to_string_lossy(),
        }))
    })
}

fn generate_face_thumbnail(core: &Core, face_id: i64, thumb_path: &Path) -> Result<(), String> {
    let (image_id, bbox_str) =
        crate::db::persons::get_face_info(&core.pool, face_id).map_err(|e| e.to_string())?;

    let image_path: Option<String> = {
        let conn = crate::db::get_conn(&core.pool).map_err(|e| e.to_string())?;
        conn.query_row("SELECT path FROM images WHERE id=?", params![image_id], |r| r.get(0))
            .ok()
    };
    let image_path = image_path.ok_or_else(|| format!("人脸 {face_id} 对应的原图不存在"))?;

    let bbox: Vec<i64> = bbox_str
        .split(',')
        .filter_map(|s| s.trim().parse::<i64>().ok())
        .collect();
    if bbox.len() != 4 {
        return Err("人脸框格式非法".to_string());
    }

    std::fs::create_dir_all(&core.config.faces_dir).map_err(|e| e.to_string())?;

    let img = crate::imageproc::io::decode_rgb(&PathBuf::from(&image_path))
        .map_err(|e| e.to_string())?;
    let (w, h) = (img.width(), img.height());

    let x1 = (bbox[0] as u32).min(w);
    let y1 = (bbox[1] as u32).min(h);
    let x2 = (bbox[2] as u32).min(w);
    let y2 = (bbox[3] as u32).min(h);
    if x2 <= x1 || y2 <= y1 {
        return Err("人脸框尺寸非法".to_string());
    }

    // 加内边距，呈现头像感（与旧实现一致：横向 20%、纵向 30%）
    let pad_x = ((x2 - x1) as f32 * 0.2) as u32;
    let pad_y = ((y2 - y1) as f32 * 0.3) as u32;
    let x1 = x1.saturating_sub(pad_x);
    let y1 = y1.saturating_sub(pad_y);
    let x2 = (x2 + pad_x).min(w);
    let y2 = (y2 + pad_y).min(h);

    let cropped = image::imageops::crop_imm(&img, x1, y1, x2 - x1, y2 - y1).to_image();
    let thumb = crate::imageproc::resize::thumbnail(&image::DynamicImage::from(cropped), 200);
    let bytes = crate::imageproc::io::encode_jpeg(&thumb, 85);
    std::fs::write(thumb_path, &bytes).map_err(|e| e.to_string())?;
    Ok(())
}

// ---------------------------------------------------------------- 小工具

fn parse_ids(s: &str) -> Vec<i64> {
    if s.trim().is_empty() {
        return vec![];
    }
    s.split(',')
        .filter_map(|x| x.trim().parse::<i64>().ok())
        .collect()
}
