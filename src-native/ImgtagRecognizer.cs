using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Data.Sqlite;

namespace PictureButler;

/// <summary>
/// imgtag 识别能力集成（人脸识别 / AI 打标）——**进程内 FFI 形态**。
///
/// 融合后不再有独立 imgtag.exe、不再监听 8520 端口；识别引擎以 <c>imgtag_native.dll</c>
/// （Rust cdylib）加载进本进程，通过 <see cref="ImgtagNative"/> 直接调用：
///   1. EnsureServer —— 初始化进程内识别引擎（须先在数据目录放置 imgtag_native.dll）
///   2. SyncNewImages —— 把自建索引中有、识别库中没有的图片直接写入识别库（SQLite）
///   3. ScanFacesAsync —— 人脸检测 + 聚类（ONNX，进程内，进度回调）
///   4. AiTagAsync —— LM Studio 视觉模型打标（Rust 侧直连 127.0.0.1:1234，进度回调）
///
/// 公开方法签名与调用方（MainWindow / ImgViewerWindow）保持**零改动**。
/// </summary>
public class ImgtagRecognizer
{
    /// <summary>
    /// 图片接口基址（人脸缩略图等）。现由程序**进程内** HTTP 服务（127.0.0.1，动态端口）提供，
    /// 不再存在独立 imgtag 服务与 8520 端口。
    /// </summary>
    public static string ServerUrl => LocalHttpServer.BaseUrl;

    public const string LmStudioUrl = "http://127.0.0.1:1234/v1";

    private static readonly object InitGate = new();

    private readonly string _imgtagDir;
    private readonly string _imgtagDb;
    private readonly string _indexDb;
    private readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

    public ImgtagRecognizer(string imgtagDir, string imgtagDb, string indexDb)
    {
        _imgtagDir = imgtagDir;
        _imgtagDb = imgtagDb;
        _indexDb = indexDb;
    }

    /// <summary>识别引擎是否已就绪（ONNX 人脸引擎就绪即可）</summary>
    public bool ServerRunning() => _faceEngine != null || ImgtagNative.IsReady();

    /// <summary>
    /// 确保识别引擎已初始化（进程内加载 imgtag_native.dll，不启动任何独立进程）。
    /// 幂等；并发调用被串行化。
    /// </summary>
    public bool EnsureServer(out string msg)
    {
        if (ImgtagNative.IsReady()) { msg = "识别引擎已就绪（进程内）"; return true; }
        lock (InitGate)
        {
            if (ImgtagNative.IsReady()) { msg = "识别引擎已就绪（进程内）"; return true; }

            // 预检已失败时别再白跑一次 init（也别抛 DllNotFound）——直接把原因交出去
            if (!ImgtagNative.PreflightOk && !string.IsNullOrEmpty(ImgtagNative.LastPreflightMessage))
            {
                msg = ImgtagNative.LastPreflightMessage!;
                return false;
            }

            ImgtagNative.SetDataDirectory(_imgtagDir);
            if (ImgtagNative.Initialize(_imgtagDir, _imgtagDb, LmStudioUrl, null, out var err))
            {
                msg = "识别引擎已就绪（进程内）";
                return true;
            }
            msg = "识别引擎不可用：" + err;
            return false;
        }
    }

    /// <summary>把识别引擎不可用的原因用对话框讲清楚（比状态栏文字可靠，不会被下次计数覆盖）。</summary>
    public static void ShowEngineUnavailable(Window owner, string? msg)
    {
        var text = msg;
        if (string.IsNullOrWhiteSpace(text)) text = "识别引擎不可用。";
        try
        {
            MessageBox.Show(owner, text, "识别引擎", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch { }
    }

    /// <summary>释放识别引擎（程序退出前调用，释放原生连接池与运行时）</summary>
    public static void Shutdown() => ImgtagNative.Shutdown();

    /// <summary>
    /// 把当前人脸参数写入识别库 settings 表（Rust 侧在初始化时读取这些值）。
    /// 三档枚举需映射为引擎实际使用的数值：
    ///   检测灵敏度：low=0.50 / standard=0.75 / high=0.85（越大越严格）
    ///   聚类灵敏度：conservative=0.70 / standard=0.55 / aggressive=0.42（越小越容易合并）
    ///   最小人脸尺寸：直接像素值（引擎内部 clamp 到 20~300）
    /// </summary>
    public bool WriteFaceSettingsToDb()
    {
        try
        {
            double det = FaceOptions.Sensitivity switch { "low" => 0.50, "high" => 0.85, _ => 0.75 };
            double sim = FaceOptions.ClusterSensitivity switch { "conservative" => 0.70, "aggressive" => 0.42, _ => 0.55 };
            long min = FaceOptions.MinFaceSize;

            using var conn = new SqliteConnection($"Data Source={_imgtagDb}");
            conn.Open();
            // 引擎可能正持有连接，设置忙等待避免一锁就失败
            using (var pr = conn.CreateCommand())
            {
                pr.CommandText = "PRAGMA busy_timeout = 8000";
                pr.ExecuteNonQuery();
            }
            void SetVal(string key, string val)
            {
                using var u = conn.CreateCommand();
                u.CommandText = "UPDATE settings SET value=$v WHERE key=$k";
                u.Parameters.AddWithValue("$k", key);
                u.Parameters.AddWithValue("$v", val);
                if (u.ExecuteNonQuery() > 0) return;
                using var ins = conn.CreateCommand();
                ins.CommandText = "INSERT INTO settings(key,value) VALUES($k,$v)";
                ins.Parameters.AddWithValue("$k", key);
                ins.Parameters.AddWithValue("$v", val);
                ins.ExecuteNonQuery();
            }
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            SetVal("face_det_thresh", det.ToString(ci));
            SetVal("face_sim_thresh", sim.ToString(ci));
            SetVal("face_min_size", min.ToString(ci));
            return true;
        }
        catch { return false; }
    }

    /// <summary>写入人脸参数并重启引擎，使其立即生效（引擎在初始化时读取 settings 表）</summary>
    public bool ApplyFaceSettings(out string msg)
    {
        WriteFaceSettingsToDb();
        try { ImgtagNative.Shutdown(); } catch { }
        return EnsureServer(out msg);
    }

    /// <summary>LM Studio（127.0.0.1:1234）是否可用（AI 打标依赖，外部软件，不在融合范围内）</summary>
    public bool LmStudioAvailable()
    {
        try
        {
            using var r = _http.GetAsync(LmStudioUrl + "/models").GetAwaiter().GetResult();
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>
    /// 把自建索引（image_index.db）中有、识别库中没有的图片写入 识别库。
    /// 返回新增数量。跨连接写 SQLite（引擎运行中，WAL 模式，短事务+重试）。
    /// </summary>
    public int SyncNewImages(out string msg)
    {
        var images = new List<(string Path, string Filename, int W, int H, long Size)>();
        try
        {
            using var ic = new SqliteConnection($"Data Source={_indexDb};Mode=ReadOnly");
            ic.Open();
            using var cmd = ic.CreateCommand();
            cmd.CommandText = "SELECT path, filename, width, height, file_size FROM images";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                images.Add((r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1),
                    r.IsDBNull(2) ? 0 : r.GetInt32(2), r.IsDBNull(3) ? 0 : r.GetInt32(3),
                    r.IsDBNull(4) ? 0 : r.GetInt64(4)));
            }
        }
        catch (Exception ex) { msg = "读取索引库失败: " + ex.Message; return 0; }

        // 识别库现有 path 集合
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var tc = new SqliteConnection($"Data Source={_imgtagDb};Mode=ReadOnly");
            tc.Open();
            using var cmd = tc.CreateCommand();
            cmd.CommandText = "SELECT path FROM images";
            using var r = cmd.ExecuteReader();
            while (r.Read()) existing.Add(r.GetString(0));
        }
        catch (Exception ex) { msg = "读取 识别库失败: " + ex.Message; return 0; }

        var missing = images.Where(x => !existing.Contains(x.Path)).ToList();
        if (missing.Count == 0) { msg = "识别库已是最新，无需同步"; return 0; }

        int inserted = 0;
        for (int i = 0; i < missing.Count; i++)
        {
            var m = missing[i];
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    using var tc = new SqliteConnection($"Data Source={_imgtagDb}");
                    tc.Open();
                    using var tx = tc.BeginTransaction();
                    using var ins = tc.CreateCommand();
                    ins.Transaction = tx;
                    ins.CommandText = """
                        INSERT OR IGNORE INTO images (path, filename, status, width, height, file_size, created_at)
                        VALUES ($p, $f, 'done', $w, $h, $s, $c)
                        """;
                    ins.Parameters.AddWithValue("$p", m.Path);
                    ins.Parameters.AddWithValue("$f", string.IsNullOrEmpty(m.Filename) ? Path.GetFileName(m.Path.TrimEnd('\\', '/')) : m.Filename);
                    ins.Parameters.AddWithValue("$w", m.W);
                    ins.Parameters.AddWithValue("$h", m.H);
                    ins.Parameters.AddWithValue("$s", m.Size);
                    ins.Parameters.AddWithValue("$c", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                    int n = ins.ExecuteNonQuery();
                    tx.Commit();
                    if (n > 0) inserted++;
                    break;
                }
                catch (Exception ex)
                {
                    if (attempt == 2 && i % 50 == 0)
                        msg = "写入 识别库遇锁，已跳过部分（" + ex.Message + "）";
                    Thread.Sleep(120);
                }
            }
            if (i % 200 == 0 && i > 0)
                msg = $"同步图片到 识别库… {i}/{missing.Count}";
        }
        msg = $"已同步 {inserted} 张新图到 识别库";
        return inserted;
    }

    public sealed class ScanResult
    {
        public int Scanned;
        public int Faces;
        public int NewPersons;
        public int Failed;
        public int Requested;
        public string Error = "";
        public bool Ok => string.IsNullOrEmpty(Error);
    }

    /// <summary>AI 打标前准备：把「无描述」的图设为 pending（供打标处理）。返回数量。</summary>
    public int MarkPendingForTagging()
    {
        int n = 0;
        try
        {
            using var tc = new SqliteConnection($"Data Source={_imgtagDb}");
            tc.Open();
            using var cmd = tc.CreateCommand();
            // done=已入库未打标；failed=上次失败重试。description 空才是待打标
            cmd.CommandText = "UPDATE images SET status='pending' WHERE (description IS NULL OR description='') AND status IN ('done','failed')";
            n = cmd.ExecuteNonQuery();
        }
        catch { }
        return n;
    }

    /// <summary>按路径查 识别库 id（不存在则先同步）</summary>
    public long FindImgTagId(string path)
    {
        try
        {
            using var tc = new SqliteConnection($"Data Source={_imgtagDb};Mode=ReadOnly");
            tc.Open();
            using var cmd = tc.CreateCommand();
            cmd.CommandText = "SELECT id FROM images WHERE path = $p LIMIT 1";
            cmd.Parameters.AddWithValue("$p", path);
            var v = cmd.ExecuteScalar();
            if (v != null && v is long id) return id;
            if (v != null && v is int id2) return id2;
        }
        catch { }
        // 同步后再查一次
        SyncNewImages(out _);
        try
        {
            using var tc = new SqliteConnection($"Data Source={_imgtagDb};Mode=ReadOnly");
            tc.Open();
            using var cmd = tc.CreateCommand();
            cmd.CommandText = "SELECT id FROM images WHERE path = $p LIMIT 1";
            cmd.Parameters.AddWithValue("$p", path);
            var v = cmd.ExecuteScalar();
            if (v != null) return Convert.ToInt64(v);
        }
        catch { }
        return -1;
    }

    /// <summary>批量：把路径集合解析为 imgtag id 逗号串（单连接建映射，避免逐张开库）。</summary>
    private string ResolveIds(IEnumerable<string> paths, out int requested)
    {
        var list = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        requested = list.Count;
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var tc = new SqliteConnection($"Data Source={_imgtagDb};Mode=ReadOnly");
            tc.Open();
            using var cmd = tc.CreateCommand();
            cmd.CommandText = "SELECT id, path FROM images";
            using var r = cmd.ExecuteReader();
            while (r.Read()) map[r.GetString(1)] = r.GetInt64(0);
        }
        catch { }
        // 映射缺失的先同步一次再补
        if (list.Any(p => !map.ContainsKey(p)))
        {
            SyncNewImages(out _);
            try
            {
                using var tc = new SqliteConnection($"Data Source={_imgtagDb};Mode=ReadOnly");
                tc.Open();
                using var cmd = tc.CreateCommand();
                cmd.CommandText = "SELECT id, path FROM images";
                using var r = cmd.ExecuteReader();
                while (r.Read()) if (!map.ContainsKey(r.GetString(1))) map[r.GetString(1)] = r.GetInt64(0);
            }
            catch { }
        }
        var ids = list.Where(map.ContainsKey).Select(p => map[p]).Distinct().ToList();
        return string.Join(",", ids);
    }

    /// <summary>确保人脸裁剪缩略图已生成（纯 C# 实现），返回文件路径；失败返回 null。</summary>
    public string? EnsureFaceThumbnail(long faceId)
    {
        try
        {
            var dir = FaceThumbnailDir;
            Directory.CreateDirectory(dir);
            var thumbPath = Path.Combine(dir, $"{faceId}.jpg");
            if (File.Exists(thumbPath)) return thumbPath;

            // 从数据库查人脸 bbox 和图片路径
            using var tc = new SqliteConnection($"Data Source={_imgtagDb};Mode=ReadOnly");
            tc.Open();
            using var cmd = tc.CreateCommand();
            cmd.CommandText = @"
                SELECT i.path, f.bbox
                FROM image_faces f
                JOIN images i ON i.id = f.image_id
                WHERE f.id = $id";
            cmd.Parameters.AddWithValue("$id", faceId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;

            string imgPath = r.GetString(0);
            string bboxStr = r.GetString(1);

            var parts = bboxStr.Split(',');
            if (parts.Length != 4) return null;
            if (!int.TryParse(parts[0], out int x1)) return null;
            if (!int.TryParse(parts[1], out int y1)) return null;
            if (!int.TryParse(parts[2], out int x2)) return null;
            if (!int.TryParse(parts[3], out int y2)) return null;

            using var bmp = new System.Drawing.Bitmap(imgPath);
            int bw = x2 - x1;
            int bh = y2 - y1;
            if (bw <= 0 || bh <= 0) return null;

            // 扩大一点边距（30%），让头像更好看
            int pad = (int)(Math.Max(bw, bh) * 0.3f);
            int cx1 = Math.Max(0, x1 - pad);
            int cy1 = Math.Max(0, y1 - pad);
            int cx2 = Math.Min(bmp.Width - 1, x2 + pad);
            int cy2 = Math.Min(bmp.Height - 1, y2 + pad);
            int cw = cx2 - cx1;
            int ch = cy2 - cy1;

            // 裁剪
            using var faceBmp = new System.Drawing.Bitmap(cw, ch);
            using (var g = System.Drawing.Graphics.FromImage(faceBmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                g.DrawImage(bmp, 0, 0,
                    new System.Drawing.RectangleF(cx1, cy1, cw, ch),
                    System.Drawing.GraphicsUnit.Pixel);
            }

            // 缩放到 160x160（保持比例，居中裁剪）
            int targetSize = 160;
            var thumb = new System.Drawing.Bitmap(targetSize, targetSize);
            using (var g = System.Drawing.Graphics.FromImage(thumb))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                g.Clear(System.Drawing.Color.FromArgb(30, 30, 30));

                float scale = Math.Max((float)targetSize / cw, (float)targetSize / ch);
                int sw = (int)(cw * scale);
                int sh = (int)(ch * scale);
                int sx = (targetSize - sw) / 2;
                int sy = (targetSize - sh) / 2;

                g.DrawImage(faceBmp, sx, sy, sw, sh);
            }

            thumb.Save(thumbPath, System.Drawing.Imaging.ImageFormat.Jpeg);
            thumb.Dispose();
            faceBmp.Dispose();

            return thumbPath;
        }
        catch { return null; }
    }

    /// <summary>人脸缩略图目录路径（{imgtagDir}\.faces）</summary>
    public string FaceThumbnailDir => Path.Combine(_imgtagDir, ".faces");

    /// <summary>
    /// 统计人脸缩略图缓存：返回 (文件数, 总字节数)。
    /// 目录不存在时返回 (0, 0)。
    /// </summary>
    public (int Count, long Bytes) FaceThumbnailStats()
    {
        var dir = FaceThumbnailDir;
        if (!Directory.Exists(dir)) return (0, 0);
        int count = 0; long bytes = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*.jpg"))
            {
                try
                {
                    var fi = new FileInfo(f);
                    count++; bytes += fi.Length;
                }
                catch { }
            }
        }
        catch { }
        return (count, bytes);
    }

    /// <summary>清理人脸缩略图缓存（删除 .faces 目录下全部 jpg），返回删除的文件数。</summary>
    public int ClearFaceThumbnails()
    {
        var dir = FaceThumbnailDir;
        if (!Directory.Exists(dir)) return 0;
        int removed = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*.jpg"))
            {
                try { File.Delete(f); removed++; }
                catch { }
            }
        }
        catch { }
        return removed;
    }

    /// <summary>人脸识别扫描选项（检测阈值、最小尺寸、聚类灵敏度等）。</summary>
    public sealed class FaceScanOptions
    {
        /// <summary>检测灵敏度：low / standard / high</summary>
        public string Sensitivity { get; set; } = "standard";
        /// <summary>最小人脸尺寸（像素边长）</summary>
        public int MinFaceSize { get; set; } = 48;
        /// <summary>聚类灵敏度：conservative / standard / aggressive</summary>
        public string ClusterSensitivity { get; set; } = "standard";
        /// <summary>是否强制重新识别（忽略已扫描状态）</summary>
        public bool Force { get; set; }
    }

    /// <summary>当前人脸识别扫描选项（可随时修改，下次扫描生效）</summary>
    public FaceScanOptions FaceOptions { get; } = new();

    /// <summary>对所选路径批量识别人脸（进程内人脸检测 + 聚类。</summary>
    public async Task<ScanResult> ScanFacesByPathsAsync(IEnumerable<string> paths, Action<string>? progress)
    {
        var ids = ResolveIds(paths, out int requested);
        if (string.IsNullOrEmpty(ids)) return new ScanResult { Error = "所选图片均不在识别库中" };
        var res = await ScanFacesAsync(null, ids, progress, force: false);
        res.Requested = requested;
        return res;
    }

    /// <summary>对所选路径批量 AI 打标（需 LM Studio）。</summary>
    public async Task<ScanResult> AiTagByPathsAsync(IEnumerable<string> paths, Action<string>? progress)
    {
        var ids = ResolveIds(paths, out int requested);
        if (string.IsNullOrEmpty(ids)) return new ScanResult { Error = "所选图片均不在识别库中" };
        var n = ids.Split(',').Length;
        var res = await AiTagAsync(null, ids, n, progress, force: false);
        res.Requested = requested;
        return res;
    }

    /// <summary>单张 AI 打标（按路径），返回 (描述, 标签)</summary>
    public async Task<(string Desc, string Tags, string Error)> AiTagOneAsync(string path, Action<string>? progress)
    {
        var id = FindImgTagId(path);
        if (id <= 0) return ("", "", "图片不在识别库中");
        var result = await AiTagAsync(null, id.ToString(), 1, progress, force: false);
        if (!result.Ok) return ("", "", result.Error);
        try
        {
            using var tc = new SqliteConnection($"Data Source={_imgtagDb};Mode=ReadOnly");
            tc.Open();
            using var cmd = tc.CreateCommand();
            cmd.CommandText = "SELECT description, tags FROM images WHERE id = $p";
            cmd.Parameters.AddWithValue("$p", id);
            using var r = cmd.ExecuteReader();
            if (r.Read())
                return (r.IsDBNull(0) ? "" : r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1), "");
        }
        catch { }
        return ("", "", "打标完成但读取结果失败");
    }

    /// <summary>单张人脸识别（按路径），返回检出人脸数</summary>
    public async Task<(int Faces, string Error)> ScanFaceOneAsync(string path, Action<string>? progress)
    {
        var id = FindImgTagId(path);
        if (id <= 0) return (0, "图片不在识别库中");
        var result = await ScanFacesAsync(null, id.ToString(), progress, force: false);
        if (!result.Ok) return (0, result.Error);
        return (result.Faces, "");
    }

    /// <summary>人脸识别扫描：进程内 ONNX 检测 + 聚类。folder 为空则扫描全部 faces_scanned=0 且非 pending 的图片（增量）。</summary>
    public async Task<ScanResult> ScanFacesAsync(string? folder, Action<string>? progress)
        => await ScanFacesAsync(folder, "", progress, force: false);

    /// <summary>人脸识别扫描：可指定是否强制重新识别全部。</summary>
    public async Task<ScanResult> ScanFacesAsync(string? folder, Action<string>? progress, bool force)
        => await ScanFacesAsync(folder, "", progress, force);

    // ---- 纯 C# ONNX 人脸引擎（替换有 bug 的 Rust DLL 检测） ----
    private FaceOnnxEngine? _faceEngine;
    private static readonly object FaceEngineGate = new();

    /// <summary>确保 ONNX 人脸引擎已初始化（独立于 Rust 引擎）</summary>
    private bool EnsureFaceEngine(out string msg)
    {
        if (_faceEngine != null) { msg = "人脸引擎已就绪"; return true; }
        lock (FaceEngineGate)
        {
            if (_faceEngine != null) { msg = "人脸引擎已就绪"; return true; }
            try
            {
                var modelDir = Path.Combine(_imgtagDir, "models");
                _faceEngine = new FaceOnnxEngine(modelDir);
                msg = "人脸引擎已就绪";
                return true;
            }
            catch (Exception ex)
            {
                msg = "人脸引擎初始化失败: " + ex.Message;
                return false;
            }
        }
    }

    /// <summary>人脸识别扫描：纯 C# ONNX 实现（SCRFD 检测 + ArcFace 特征 + 聚类）。
    /// folder 为空则扫描全部 faces_scanned=0 的图片（增量）。force=true 强制重扫全部。</summary>
    private async Task<ScanResult> ScanFacesAsync(string? folder, string imageIds, Action<string>? progress, bool force)
    {
        var result = new ScanResult();
        if (!EnsureFaceEngine(out var err))
        {
            result.Error = err;
            return result;
        }

        // 1. 获取待扫描图片列表
        var images = new List<(long Id, string Path, int W, int H)>();
        try
        {
            using var tc = new SqliteConnection($"Data Source={_imgtagDb}");
            tc.Open();
            using var cmd = tc.CreateCommand();

            if (!string.IsNullOrEmpty(imageIds))
            {
                // 指定 ID 扫描
                var idList = imageIds.Split(',', StringSplitOptions.RemoveEmptyEntries);
                cmd.CommandText = "SELECT id, path, width, height FROM images WHERE id IN (" +
                    string.Join(",", idList.Select((_, i) => $"${i}")) + ")";
                for (int i = 0; i < idList.Length; i++)
                    cmd.Parameters.AddWithValue($"${i}", long.Parse(idList[i]));
            }
            else if (!string.IsNullOrEmpty(folder))
            {
                // 指定文件夹
                cmd.CommandText = force
                    ? "SELECT id, path, width, height FROM images WHERE path LIKE $p ORDER BY id"
                    : "SELECT id, path, width, height FROM images WHERE path LIKE $p AND faces_scanned=0 ORDER BY id";
                cmd.Parameters.AddWithValue("$p", folder + "%");
            }
            else
            {
                // 全量
                cmd.CommandText = force
                    ? "SELECT id, path, width, height FROM images ORDER BY id"
                    : "SELECT id, path, width, height FROM images WHERE faces_scanned=0 ORDER BY id";
            }

            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                images.Add((r.GetInt64(0), r.GetString(1),
                    r.IsDBNull(2) ? 0 : r.GetInt32(2),
                    r.IsDBNull(3) ? 0 : r.GetInt32(3)));
            }
        }
        catch (Exception ex)
        {
            result.Error = "读取待扫描图片失败: " + ex.Message;
            return result;
        }

        if (images.Count == 0)
        {
            result.Scanned = 0;
            result.Faces = 0;
            return result;
        }

        result.Requested = images.Count;

        // 2. 逐张检测 + 提取特征
        // 每批处理一张图片（避免内存占用过大），收集所有人脸 embedding 用于后续聚类
        var allFaceData = new List<(long ImageId, FaceDetection Face, float[] Embedding)>();
        int scanned = 0;
        int failed = 0;

        await Task.Run(() =>
        {
            foreach (var (id, path, w, h) in images)
            {
                scanned++;
                try
                {
                    // 检测
                    var faces = _faceEngine!.Detect(path);
                    if (faces.Count == 0)
                    {
                        // 无人脸，标记已扫描
                        UpdateFacesScanned(id, scanned: 1);
                        progress?.Invoke($"人脸识别中… {scanned}/{images.Count}");
                        continue;
                    }

                    // 提取每张人脸的特征
                    using var bmp = new System.Drawing.Bitmap(path);
                    foreach (var face in faces)
                    {
                        var emb = _faceEngine.GetEmbedding(bmp, face);
                        allFaceData.Add((id, face, emb));
                    }

                    // 标记已扫描
                    UpdateFacesScanned(id, scanned: 1);
                }
                catch
                {
                    failed++;
                }

                if (scanned % 10 == 0 || scanned == images.Count)
                    progress?.Invoke($"人脸识别中… {scanned}/{images.Count}");
            }
        }).ConfigureAwait(false);

        result.Scanned = scanned;
        result.Failed = failed;
        result.Faces = allFaceData.Count;

        if (allFaceData.Count == 0) return result;

        // 3. 人脸聚类
        progress?.Invoke("人脸聚类中…");
        var embeddings = allFaceData.Select(f => f.Embedding).ToList();
        var clusterThresh = FaceOptions.ClusterSensitivity switch
        {
            "conservative" => 0.68f,  // 保守：相似度要求高
            "aggressive" => 0.52f,    // 积极：相似度要求低
            _ => 0.60f                 // 标准
        };
        var labels = FaceOnnxEngine.Cluster(embeddings, clusterThresh);

        // 4. 写入数据库
        progress?.Invoke("写入数据库中…");
        try
        {
            using var tc = new SqliteConnection($"Data Source={_imgtagDb}");
            tc.Open();
            using var tx = tc.BeginTransaction();

            // 4a. 先删除这些图片已有的人脸记录（force 模式或增量都清理）
            // 但增量模式下图片之前 faces_scanned=0，应该没有人脸记录
            if (force || !string.IsNullOrEmpty(imageIds))
            {
                var delCmd = tc.CreateCommand();
                delCmd.CommandText = "DELETE FROM image_faces WHERE image_id IN (" +
                    string.Join(",", images.Select(i => i.Id)) + ")";
                delCmd.Transaction = tx;
                delCmd.ExecuteNonQuery();
            }

            // 4b. 获取现有 persons 的 embedding（用于匹配已有聚类）
            var existingPersons = new List<(long Id, string Name, float[] AvgEmb, int FaceCount)>();
            try
            {
                var pCmd = tc.CreateCommand();
                pCmd.Transaction = tx;
                pCmd.CommandText = @"
                    SELECT p.id, p.name, p.avatar_face_id,
                           (SELECT COUNT(*) FROM image_faces f WHERE f.person_id = p.id) as cnt
                    FROM persons p
                    WHERE p.ignored = 0
                    ORDER BY cnt DESC";
                using var pr = pCmd.ExecuteReader();
                var personIds = new List<long>();
                var personNames = new List<string>();
                var personFaceCounts = new List<int>();
                while (pr.Read())
                {
                    personIds.Add(pr.GetInt64(0));
                    personNames.Add(pr.IsDBNull(1) ? "" : pr.GetString(1));
                    personFaceCounts.Add(pr.GetInt32(3));
                }
                pr.Close();

                // 取每个人的代表 embedding（多人脸取均值，匹配更稳）
                for (int i = 0; i < personIds.Count; i++)
                {
                    var fCmd = tc.CreateCommand();
                    fCmd.Transaction = tx;
                    fCmd.CommandText = "SELECT embedding FROM image_faces WHERE person_id=$p AND embedding IS NOT NULL LIMIT 12";
                    fCmd.Parameters.AddWithValue("$p", personIds[i]);
                    var vecs = new List<float[]>();
                    using (var fr = fCmd.ExecuteReader())
                    {
                        while (fr.Read())
                        {
                            if (fr.IsDBNull(0)) continue;
                            var b = (byte[])fr.GetValue(0);
                            if (b.Length < 128 * 4) continue;
                            var emb = new float[b.Length / 4];
                            Buffer.BlockCopy(b, 0, emb, 0, b.Length);
                            float norm = 0;
                            foreach (var v in emb) norm += v * v;
                            norm = MathF.Sqrt(norm);
                            if (norm > 0) for (int j = 0; j < emb.Length; j++) emb[j] /= norm;
                            vecs.Add(emb);
                        }
                    }
                    if (vecs.Count == 0) continue;
                    var dim = vecs[0].Length;
                    var mean = new float[dim];
                    foreach (var v in vecs)
                        for (int j = 0; j < dim; j++) mean[j] += v[j];
                    float avgNorm = 0;
                    foreach (var x in mean) avgNorm += x * x;
                    avgNorm = MathF.Sqrt(avgNorm);
                    if (avgNorm > 0) for (int j = 0; j < dim; j++) mean[j] /= avgNorm;

                    existingPersons.Add((personIds[i], personNames[i], mean, personFaceCounts[i]));
                }
            }
            catch { }

            // 4b-2. 加载「已忽略人物」的代表特征：与它们相似的人脸不再入库（永久忽略）
            var ignoredEmbs = new List<float[]>();
            try
            {
                var igCmd = tc.CreateCommand();
                igCmd.Transaction = tx;
                igCmd.CommandText = @"
                    SELECT f.embedding FROM image_faces f
                    JOIN persons p ON p.id = f.person_id
                    WHERE p.ignored = 1 AND f.embedding IS NOT NULL";
                using var igr = igCmd.ExecuteReader();
                while (igr.Read())
                {
                    if (igr.IsDBNull(0)) continue;
                    var b = (byte[])igr.GetValue(0);
                    if (b.Length < 128) continue;
                    var e = new float[b.Length / 4];
                    Buffer.BlockCopy(b, 0, e, 0, b.Length);
                    float n2 = 0;
                    foreach (var v in e) n2 += v * v;
                    n2 = MathF.Sqrt(n2);
                    if (n2 > 0) for (int j = 0; j < e.Length; j++) e[j] /= n2;
                    ignoredEmbs.Add(e);
                }
            }
            catch { }

            // 4c. 把新聚类匹配到已有人物，或创建新人物
            // 先用聚类中心（每个聚类的平均 embedding）去匹配现有人员
            int numClusters = labels.Max() + 1;
            var clusterToPersonId = new Dictionary<int, long>();

            for (int c = 0; c < numClusters; c++)
            {
                // 计算聚类平均 embedding
                var clusterIndices = new List<int>();
                for (int i = 0; i < labels.Count; i++)
                    if (labels[i] == c) clusterIndices.Add(i);

                if (clusterIndices.Count == 0) continue;

                var avgEmb = new float[allFaceData[0].Embedding.Length];
                foreach (var idx in clusterIndices)
                {
                    var emb = allFaceData[idx].Embedding;
                    for (int j = 0; j < emb.Length; j++)
                        avgEmb[j] += emb[j];
                }
                float avgNorm = 0;
                foreach (var v in avgEmb) avgNorm += v * v;
                avgNorm = MathF.Sqrt(avgNorm);
                if (avgNorm > 0) for (int j = 0; j < avgEmb.Length; j++) avgEmb[j] /= avgNorm;

                // 找最相似的已有人员（有名字的人物阈值放宽，避免新图又冒出一堆无名人物）
                long bestPersonId = -1;
                float bestSim = -1f;
                foreach (var (id, name, emb, cnt) in existingPersons)
                {
                    float need = string.IsNullOrWhiteSpace(name) ? clusterThresh : Math.Min(clusterThresh, 0.50f);
                    int len = Math.Min(avgEmb.Length, emb.Length);
                    float sim = 0;
                    for (int j = 0; j < len; j++) sim += avgEmb[j] * emb[j];
                    if (sim > need && sim > bestSim)
                    {
                        bestSim = sim;
                        bestPersonId = id;
                    }
                }

                if (bestPersonId > 0)
                {
                    clusterToPersonId[c] = bestPersonId;
                }
                else
                {
                    // 创建新人物（自动命名「人物N」，与 Rust next_person_name 逻辑一致，不依赖 AI 打标）
                    var newName = NextPersonName(tc);
                    var insCmd = tc.CreateCommand();
                    insCmd.Transaction = tx;
                    insCmd.CommandText = "INSERT INTO persons (name, ignored) VALUES ($n, 0); SELECT last_insert_rowid();";
                    insCmd.Parameters.AddWithValue("$n", newName);
                    var newId = Convert.ToInt64(insCmd.ExecuteScalar());
                    clusterToPersonId[c] = newId;
                }
            }

            // 4d. 写入人脸记录
            int skippedIgnored = 0;
            var insertCmd = tc.CreateCommand();
            insertCmd.Transaction = tx;
            insertCmd.CommandText = @"
                INSERT INTO image_faces (image_id, person_id, bbox, score, embedding, created_at)
                VALUES ($image_id, $person_id, $bbox, $score, $embedding, datetime('now'))";
            var pImageId = insertCmd.Parameters.Add("$image_id", SqliteType.Integer);
            var pPersonId = insertCmd.Parameters.Add("$person_id", SqliteType.Integer);
            var pBbox = insertCmd.Parameters.Add("$bbox", SqliteType.Text);
            var pScore = insertCmd.Parameters.Add("$score", SqliteType.Real);
            var pEmb = insertCmd.Parameters.Add("$embedding", SqliteType.Blob);

            for (int i = 0; i < allFaceData.Count; i++)
            {
                var (imgId, face, emb) = allFaceData[i];
                int cluster = labels[i];
                long personId = clusterToPersonId.ContainsKey(cluster) ? clusterToPersonId[cluster] : 0;

                // 永久忽略：与「已忽略人物」高度相似的人脸不入库，下次扫描也不会再冒出来
                if (ignoredEmbs.Count > 0 && MatchesIgnored(ignoredEmbs, emb, clusterThresh))
                {
                    skippedIgnored++;
                    continue;
                }

                string bboxStr = $"{(int)face.X1},{(int)face.Y1},{(int)face.X2},{(int)face.Y2}";
                byte[] embBytes = new byte[emb.Length * 4];
                Buffer.BlockCopy(emb, 0, embBytes, 0, embBytes.Length);

                pImageId.Value = imgId;
                // person_id 绝不能写 NULL：否则人脸检测出来了也不进「人物」页
                long pid = personId;
                if (pid <= 0)
                {
                    var mk = tc.CreateCommand();
                    mk.Transaction = tx;
                    mk.CommandText = "INSERT INTO persons (name, ignored) VALUES ($n, 0); SELECT last_insert_rowid();";
                    mk.Parameters.AddWithValue("$n", NextPersonName(tc));
                    pid = Convert.ToInt64(mk.ExecuteScalar());
                    if (cluster >= 0) clusterToPersonId[cluster] = pid;
                }
                pPersonId.Value = pid;
                pBbox.Value = bboxStr;
                pScore.Value = face.Score;
                pEmb.Value = embBytes;
                insertCmd.ExecuteNonQuery();
            }

            // 4e. 更新 faces_scanned 标记（如果之前没更新的话）
            var scanCmd = tc.CreateCommand();
            scanCmd.Transaction = tx;
            scanCmd.CommandText = "UPDATE images SET faces_scanned = 1 WHERE id IN (" +
                string.Join(",", images.Select(i => i.Id)) + ")";
            scanCmd.ExecuteNonQuery();

            tx.Commit();
        }
        catch (Exception ex)
        {
            result.Error = "写入数据库失败: " + ex.Message;
            return result;
        }

        // 收尾：补人物头像 + 把悬空人脸归入人物（识别人脸后立即进人物页，不依赖 AI 打标）
        try { FinalizePersonAssignments(); } catch { }

        // 重新统计人脸数
        try
        {
            using var tc = new SqliteConnection($"Data Source={_imgtagDb};Mode=ReadOnly");
            tc.Open();
            using var cmd = tc.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM image_faces";
            result.Faces = Convert.ToInt32(cmd.ExecuteScalar());
        }
        catch { }

        return result;
    }

    /// <summary>
    /// 生成下一个自动人物名「人物N」（照抄 Rust next_person_name：取现有最大编号 +1，保证不冲突）。
    /// 在事务连接上调用，随事务提交。
    /// </summary>
    private static string NextPersonName(SqliteConnection conn)
    {
        long maxN = 0;
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM persons WHERE name LIKE '人物%'";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var name = r.IsDBNull(0) ? "" : r.GetString(0);
                if (name.StartsWith("人物") && name.Length > 2 &&
                    long.TryParse(name.AsSpan(2), out var n))
                {
                    if (n > maxN) maxN = n;
                }
            }
        }
        catch { }
        return $"人物{maxN + 1}";
    }

    /// <summary>
    /// 识别人脸后的收尾：
    /// 1) person_id 为空的人脸按特征归入已有人物（避免“检出了人脸却不进人物”）
    /// 2) 没有人物头像的补上 avatar_face_id（人物卡片才出缩略图）
    /// 3) 空名人物自动补名「人物N」（人脸识别生成的人物不依赖 AI 打标即有名字）
    /// </summary>
    private void FinalizePersonAssignments()
    {
        using var tc = new SqliteConnection($"Data Source={_imgtagDb}");
        tc.Open();

        // 1) 悬空人脸 → 最相似人物（阈值 0.55）
        const float thresh = 0.55f;
        var pmap = new Dictionary<long, float[]>();
        using (var pc = tc.CreateCommand())
        {
            pc.CommandText = "SELECT id FROM persons WHERE ignored=0";
            using var pr = pc.ExecuteReader();
            while (pr.Read()) pmap[pr.GetInt64(0)] = Array.Empty<float>();
        }
        foreach (var pid in pmap.Keys.ToList())
        {
            var vecs = new List<float[]>();
            using var fc = tc.CreateCommand();
            fc.CommandText = "SELECT embedding FROM image_faces WHERE person_id=$p AND embedding IS NOT NULL LIMIT 8";
            fc.Parameters.AddWithValue("$p", pid);
            using var fr = fc.ExecuteReader();
            while (fr.Read())
            {
                if (fr.IsDBNull(0)) continue;
                var e = BlobToL2((byte[])fr.GetValue(0));
                if (e != null) vecs.Add(e);
            }
            if (vecs.Count == 0) { pmap.Remove(pid); continue; }
            var dim = vecs[0].Length;
            var mean = new float[dim];
            foreach (var v in vecs) for (int i = 0; i < dim; i++) mean[i] += v[i];
            float n = 0; foreach (var x in mean) n += x * x;
            n = MathF.Sqrt(n);
            if (n > 0) for (int i = 0; i < dim; i++) mean[i] /= n;
            pmap[pid] = mean;
        }

        var orphans = new List<(long FaceId, float[] Emb)>();
        using (var oc = tc.CreateCommand())
        {
            oc.CommandText = "SELECT id, embedding FROM image_faces WHERE person_id IS NULL AND embedding IS NOT NULL";
            using var or_ = oc.ExecuteReader();
            while (or_.Read())
            {
                if (or_.IsDBNull(1)) continue;
                var e = BlobToL2((byte[])or_.GetValue(1));
                if (e != null) orphans.Add((or_.GetInt64(0), e));
            }
        }
        foreach (var (fid, emb) in orphans)
        {
            long best = -1; float bestSim = thresh;
            foreach (var (pid, refEmb) in pmap)
            {
                if (refEmb.Length == 0) continue;
                int len = Math.Min(emb.Length, refEmb.Length);
                float s = 0;
                for (int i = 0; i < len; i++) s += emb[i] * refEmb[i];
                if (s > bestSim) { bestSim = s; best = pid; }
            }
            if (best < 0)
            {
                // 仍匹配不到 → 新建命名人物，绝不再留空名/悬空
                using var mk = tc.CreateCommand();
                mk.CommandText = "INSERT INTO persons (name, ignored) VALUES ($n, 0); SELECT last_insert_rowid();";
                mk.Parameters.AddWithValue("$n", NextPersonName(tc));
                best = Convert.ToInt64(mk.ExecuteScalar());
                pmap[best] = emb;
            }
            using var up = tc.CreateCommand();
            up.CommandText = "UPDATE image_faces SET person_id=$p WHERE id=$f";
            up.Parameters.AddWithValue("$p", best);
            up.Parameters.AddWithValue("$f", fid);
            up.ExecuteNonQuery();
        }

        // 2) 空名人物补上「人物N」
        using (var nm = tc.CreateCommand())
        {
            nm.CommandText = "SELECT id FROM persons WHERE name IS NULL OR name=''";
            var ids = new List<long>();
            using (var r = nm.ExecuteReader())
                while (r.Read()) ids.Add(r.GetInt64(0));
            foreach (var pid in ids)
            {
                using var up = tc.CreateCommand();
                up.CommandText = "UPDATE persons SET name=$n WHERE id=$p";
                up.Parameters.AddWithValue("$n", NextPersonName(tc));
                up.Parameters.AddWithValue("$p", pid);
                up.ExecuteNonQuery();
            }
        }

        // 2) 补人物头像：取该人物分数最高的一张人脸
        using (var av = tc.CreateCommand())
        {
            av.CommandText = """
                UPDATE persons SET avatar_face_id = (
                    SELECT f.id FROM image_faces f
                    WHERE f.person_id = persons.id
                    ORDER BY f.score DESC, f.id ASC LIMIT 1
                )
                WHERE (avatar_face_id IS NULL OR avatar_face_id = 0)
                  AND EXISTS (SELECT 1 FROM image_faces f2 WHERE f2.person_id = persons.id)
                """;
            av.ExecuteNonQuery();
        }

        // 3) 空名人物自动补名「人物N」（人脸识别生成的人物不依赖 AI 打标即有名字）
        try { NameUnnamedPersons(); } catch { }
    }

    /// <summary>
    /// 给所有空名人物（name 为空）自动补名「人物N」。
    /// 独立于识别扫描，可在启动时调用，保证历史遗留的空名人物也有名字。
    /// 已命名人物（含「人物N」与真实姓名）不受影响。
    /// </summary>
    public void NameUnnamedPersons()
    {
        var unnamed = new List<long>();
        using (var tc = new SqliteConnection($"Data Source={_imgtagDb}"))
        {
            tc.Open();
            using (var nc = tc.CreateCommand())
            {
                nc.CommandText = "SELECT id FROM persons WHERE (name IS NULL OR name = '') AND ignored = 0";
                using var nr = nc.ExecuteReader();
                while (nr.Read()) unnamed.Add(nr.GetInt64(0));
            }
            foreach (var pid in unnamed)
            {
                using var up = tc.CreateCommand();
                up.CommandText = "UPDATE persons SET name=$n WHERE id=$p";
                up.Parameters.AddWithValue("$n", NextPersonName(tc));
                up.Parameters.AddWithValue("$p", pid);
                up.ExecuteNonQuery();
            }
        }
    }

    private static float[]? BlobToL2(byte[] blob)
    {
        if (blob == null || blob.Length < 128 * 4) return null;
        int n = blob.Length / 4;
        var emb = new float[n];
        Buffer.BlockCopy(blob, 0, emb, 0, n * 4);
        float norm = 0;
        foreach (var v in emb) norm += v * v;
        norm = MathF.Sqrt(norm);
        if (norm <= 0) return null;
        for (int i = 0; i < n; i++) emb[i] /= norm;
        return emb;
    }

    /// <summary>判断人脸特征是否命中任一「已忽略人物」的特征（余弦相似度 ≥ 阈值即视为同一人）</summary>
    private static bool MatchesIgnored(List<float[]> ignored, float[] emb, float threshold)
    {
        foreach (var ig in ignored)
        {
            int len = Math.Min(ig.Length, emb.Length);
            if (len == 0) continue;
            float sim = 0;
            for (int i = 0; i < len; i++) sim += ig[i] * emb[i];
            if (sim >= threshold) return true;
        }
        return false;
    }

    /// <summary>更新单张图片的 faces_scanned 状态（短事务）</summary>
    private void UpdateFacesScanned(long imageId, int scanned)
    {
        try
        {
            using var tc = new SqliteConnection($"Data Source={_imgtagDb}");
            tc.Open();
            using var cmd = tc.CreateCommand();
            cmd.CommandText = "UPDATE images SET faces_scanned = $s WHERE id = $id";
            cmd.Parameters.AddWithValue("$s", scanned);
            cmd.Parameters.AddWithValue("$id", imageId);
            cmd.ExecuteNonQuery();
        }
        catch { }
    }

    /// <summary>AI 打标：进程内调度 + Rust 侧直连 LM Studio（需 LM Studio）。
    /// folder 为空则处理全部 pending 图片。</summary>
    public async Task<ScanResult> AiTagAsync(string? folder, int limit, Action<string>? progress)
        => await AiTagAsync(folder, "", limit, progress, force: false);

    /// <summary>AI 打标（可强制全部重打）。
    /// <paramref name="force"/>=true 时不筛 status，对全部图片重新打标（耗时很长）。</summary>
    public async Task<ScanResult> AiTagAsync(string? folder, int limit, Action<string>? progress, bool force)
        => await AiTagAsync(folder, "", limit, progress, force);

    private async Task<ScanResult> AiTagAsync(string? folder, string imageIds, int limit, Action<string>? progress, bool force)
    {
        var result = new ScanResult();
        if (!ImgtagNative.IsReady())
        {
            result.Error = "识别引擎未初始化";
            return result;
        }
        var body = JsonSerializer.Serialize(new { folder = folder ?? "", image_ids = imageIds, limit, force });
        var nat = await Task.Run(() =>
            ImgtagNative.AiTag(body, m => ReportProgress(m, progress, "AI 打标中…"))).ConfigureAwait(false);

        result.Scanned = nat.Scanned;
        result.Failed = nat.Failed;
        if (!nat.Ok) result.Error = string.IsNullOrEmpty(nat.Error) ? "打标出错" : nat.Error;
        return result;
    }

    /// <summary>
    /// 解析原生进度事件（JSON 字段与旧 SSE <c>data:</c> 行**同构**），转成界面文案。
    /// 仅处理 <c>type=="progress"</c>，其余事件（start/done/error）由返回值统一体现。
    /// </summary>
    private static void ReportProgress(string json, Action<string>? progress, string verb)
    {
        if (progress == null || string.IsNullOrEmpty(json)) return;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var t) || t.GetString() != "progress") return;
            var cur = root.TryGetProperty("current", out var c) ? c.GetInt32() : 0;
            var tot = root.TryGetProperty("total", out var tt) ? tt.GetInt32() : 0;
            progress($"{verb} {cur}/{tot}");
        }
        catch { }
    }
}
