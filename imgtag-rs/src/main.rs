//! ImgTag - Local AI photo album manager (Rust rewrite)
//!
//! Entry point: argument parsing, environment setup, server startup.
//!
//! 注意：核心实现位于库 crate `imgtag_native`（`src/lib.rs`），本 bin 仅作
//! 开发调试与「独立服务形态」保留用。生产环境由 PictureButler 进程内调用 cdylib。

use std::path::PathBuf;
use std::sync::Arc;

use clap::Parser;
use tracing_subscriber::EnvFilter;

use imgtag_native::{api, db, face, llm};
use imgtag_native::config::{info, warn, AppConfig};

#[derive(Parser, Debug, Clone)]
#[command(name = "imgtag", version, about = "ImgTag - Local AI photo album manager")]
pub struct Cli {
    /// Listen address
    #[arg(long, default_value = "127.0.0.1", env = "IMGTAG_HOST")]
    host: String,

    /// Listen port
    #[arg(long, default_value_t = 8520, env = "IMGTAG_PORT")]
    port: u16,

    /// Do not auto-open browser
    #[arg(long, env = "IMGTAG_NO_BROWSER")]
    no_browser: bool,

    /// LM Studio API URL
    #[arg(long, default_value = "http://127.0.0.1:1234/v1", env = "LM_STUDIO_URL")]
    lm_url: String,

    /// Skip LM Studio connectivity check
    #[arg(long)]
    skip_lm_check: bool,

    /// Project base directory (where db.sqlite, models/, static/ live)
    #[arg(long, env = "IMGTAG_BASE_DIR")]
    base_dir: Option<PathBuf>,
}

#[tokio::main]
async fn main() -> anyhow::Result<()> {
    // Initialize logging
    tracing_subscriber::fmt()
        .with_env_filter(EnvFilter::try_from_default_env().unwrap_or_else(|_| EnvFilter::new("info")))
        .with_target(false)
        .init();

    let cli = Cli::parse();

    // Determine base directory.
    // Priority: --base-dir flag > cwd (if it has index.html) > exe parent.
    let base_dir = cli.base_dir.clone().unwrap_or_else(|| {
        let exe_parent = std::env::current_exe()
            .ok()
            .and_then(|p| p.parent().map(PathBuf::from));
        let cwd = std::env::current_dir().ok();
        // Prefer cwd if it contains index.html (typical: run from project root)
        if let Some(ref cwd) = cwd {
            if cwd.join("index.html").exists() {
                return cwd.clone();
            }
        }
        // Otherwise fall back to exe parent (portable: exe sits next to index.html)
        exe_parent.unwrap_or_else(|| cwd.unwrap_or_else(|| PathBuf::from(".")))
    });

    info(&format!("ImgTag starting, base_dir = {}", base_dir.display()));

    // Portable cache redirection (matching run.py behavior)
    setup_portable_env(&base_dir);

    // Initialize config
    let config = AppConfig::new(base_dir.clone());
    info(&format!("db path = {}", config.db_path.display()));
    info(&format!("faces dir = {}", config.faces_dir.display()));
    info(&format!("thumbs dir = {}", config.thumbs_dir.display()));

    // Ensure directories exist
    std::fs::create_dir_all(&config.faces_dir)?;
    std::fs::create_dir_all(&config.thumbs_dir)?;
    std::fs::create_dir_all(&config.cache_dir)?;

    // Initialize database
    let pool = db::init_pool(&config.db_path)?;
    db::init_db(&pool)?;
    db::load_settings_into_config(&pool, &config)?;
    // Backfill missing image metadata in the background (non-blocking)
    db::spawn_backfill_task(pool.clone());

    info("Database initialized");

    // Check LM Studio (non-blocking on failure)
    if !cli.skip_lm_check {
        if let Err(e) = llm::check_lm_studio(&cli.lm_url).await {
            warn(&format!("LM Studio not reachable: {e}"));
            warn("AI auto-tagging will be unavailable; browsing still works");
        }
    }

    // Initialize face recognition (lazy; will load models on first use)
    let face_app = face::FaceApp::new(&config);
    let face_state = Arc::new(face_app);

    // Build application state
    let state = Arc::new(api::AppState {
        config: config.clone(),
        db: pool,
        face: face_state,
    });

    // Build router
    let app = api::build_router(state.clone());

    // Start server
    let url = format!("http://{}:{}", cli.host, cli.port);
    info(&format!("listening on {}", url));
    info("Press Ctrl+C to stop");
    println!("{}", "-".repeat(60));

    // Auto-open browser
    if !cli.no_browser {
        let url_clone = url.clone();
        tokio::spawn(async move {
            tokio::time::sleep(std::time::Duration::from_millis(1200)).await;
            let _ = webbrowser::open(&url_clone);
        });
    }

    // Graceful shutdown
    let shutdown = async {
        tokio::signal::ctrl_c()
            .await
            .expect("failed to install Ctrl+C handler");
        info("Received Ctrl+C, shutting down...");
    };

    let listener = tokio::net::TcpListener::bind((cli.host.as_str(), cli.port)).await?;
    axum::serve(listener, app)
        .with_graceful_shutdown(shutdown)
        .await?;

    info("Server stopped");
    Ok(())
}

fn setup_portable_env(base_dir: &std::path::Path) {
    let cache_dir = base_dir.join(".cache");
    let _ = std::fs::create_dir_all(&cache_dir);
    // Redirect various caches to project dir (portability)
    if std::env::var("PIP_CACHE_DIR").is_err() {
        std::env::set_var("PIP_CACHE_DIR", &cache_dir);
    }
    if std::env::var("HF_HOME").is_err() {
        std::env::set_var("HF_HOME", cache_dir.join("huggingface"));
    }
    if std::env::var("TORCH_HOME").is_err() {
        std::env::set_var("TORCH_HOME", cache_dir.join("torch"));
    }
    if std::env::var("INSIGHTFACE_HOME").is_err() {
        std::env::set_var("INSIGHTFACE_HOME", base_dir);
    }
    // ort: point to onnxruntime.dll if bundled alongside
    if std::env::var("ORT_DYLIB_PATH").is_err() {
        let ort_path = base_dir.join("onnxruntime.dll");
        if ort_path.exists() {
            std::env::set_var("ORT_DYLIB_PATH", &ort_path);
        }
    }
}

// Simple webbrowser stub (will be replaced by the webbrowser crate)
mod webbrowser {
    pub fn open(url: &str) -> std::io::Result<()> {
        #[cfg(target_os = "windows")]
        {
            std::process::Command::new("cmd")
                .args(["/C", "start", "", url])
                .spawn()
                .map(|_| ())
        }
        #[cfg(not(target_os = "windows"))]
        {
            let _ = url;
            Ok(())
        }
    }
}
