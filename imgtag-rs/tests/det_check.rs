//! 集成验证：用修复后的 Rust 检测器检测真实图片，与 C# 引擎写入数据库的 bbox 对照。
//! 仅读取图片与模型，不写数据库。

use imgtag_native::face::detector::FaceDetector;
use std::path::Path;

#[test]
fn detect_matches_csharp() {
    let ort = r"D:\Tool\PictureButler\imgtag\onnxruntime.dll";
    std::env::set_var("ORT_DYLIB_PATH", ort);

    let model_dir = Path::new(r"D:\Tool\PictureButler\imgtag\models\buffalo_l");
    let list = std::fs::read_to_string(r"E:\work\PictureButler\.workbuddy\test_imgs.txt")
        .expect("读取 test_imgs.txt 失败");

    let mut det = FaceDetector::new(model_dir).expect("模型加载失败");

    let mut report = String::new();
    for line in list.lines() {
        let path = line.trim();
        if path.is_empty() {
            continue;
        }
        match det.detect(Path::new(path), 0.5, 48) {
            Ok(faces) => {
                let mut s = format!("{} => {} face(s)", path, faces.len());
                for f in faces.iter().take(3) {
                    s.push_str(&format!(
                        "\n    bbox=[{},{},{},{}] score={:.3}",
                        f.bbox[0], f.bbox[1], f.bbox[2], f.bbox[3], f.score
                    ));
                }
                report.push_str(&s);
                report.push('\n');
            }
            Err(e) => {
                report.push_str(&format!("{} => ERROR: {}\n", path, e));
            }
        }
    }

    std::fs::write(r"E:\work\PictureButler\.workbuddy\rust_det_result.txt", &report)
        .expect("写结果文件失败");
    println!("{}", report);
}
