using System.IO;
using System.Text.Json;

namespace PictureButler;

/// <summary>原生版设置：持久化到 %APPDATA%\com.picturebutler.app\pb_native_settings.json</summary>
public class AppSettings
{
    public bool AutoStart { get; set; }
    public string CardSize { get; set; } = "standard";   // compact / standard / roomy
    /// <summary>提示词视图：false = 列表卡片（图 + 文字）；true = 图片墙（只显示图片，详情看右侧面板）</summary>
    public bool PromptWallMode { get; set; }
    // （0.62.0 移除 UseCustomCursor：光标固定为专属棱角箭头，手型已废弃，不再提供开关）
    /// <summary>界面主题：dark / light。切换后需重启应用生效（见 ThemeManager 说明）</summary>
    public string Theme { get; set; } = "dark";
    public string ImgtagDbPath { get; set; } = "";       // 空 = 自动定位（程序目录 imgtag\db.sqlite 优先）
    public int ImgPageSize { get; set; } = 60;           // 30 / 60 / 90
    public List<string> ImageFolders { get; set; } = new();   // 图片文件夹来源（自建索引）
    public bool ComfyMonitorEnabled { get; set; }             // ComfyUI 监听开关
    public string ComfyUrl { get; set; } = "http://127.0.0.1:8188";

    // ---- 人脸识别 ----
    /// <summary>检测灵敏度：low / standard / high（low=阈值低漏检少误检多；high=阈值高严格误检少）</summary>
    public string FaceDetectSensitivity { get; set; } = "standard";
    /// <summary>最小人脸尺寸（像素边），默认 48；越小检测越小的人脸，速度越慢</summary>
    public int FaceMinSize { get; set; } = 48;
    /// <summary>新入库图片是否自动识别人脸（添加文件夹 / ComfyUI 入库后触发）</summary>
    public bool FaceAutoRecognize { get; set; } = true;
    /// <summary>人物聚类灵敏度：conservative / standard / aggressive</summary>
    public string FaceClusterSensitivity { get; set; } = "standard";

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "com.picturebutler.app", "pb_native_settings.json");

    private static string BakPath => FilePath + ".bak";

    /// <summary>是否允许把「来源列表」存成空（仅用户手动移除全部来源时置 true）。</summary>
    public bool AllowEmptyImageFolders { get; set; }

    private static AppSettings? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
        }
        catch { return null; }
    }

    public static AppSettings Load()
    {
        var s = TryRead(FilePath) ?? TryRead(BakPath);
        if (s == null) return new AppSettings();
        // 来源兜底：主文件被清空时从 .bak 恢复，避免「每次启动都要重设来源」
        if ((s.ImageFolders == null || s.ImageFolders.Count == 0) && !s.AllowEmptyImageFolders)
        {
            var bak = TryRead(BakPath);
            if (bak?.ImageFolders?.Count > 0) s.ImageFolders = bak.ImageFolders;
        }
        s.ImageFolders ??= new List<string>();
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            // 防呆：空来源不覆盖已有来源（误加载/误序列化不会把用户配置抹掉）
            if (!AllowEmptyImageFolders && (ImageFolders == null || ImageFolders.Count == 0))
            {
                var old = TryRead(FilePath) ?? TryRead(BakPath);
                if (old?.ImageFolders?.Count > 0) ImageFolders = old.ImageFolders;
            }
            ImageFolders ??= new List<string>();

            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            // 先备份当前主文件，再原子替换
            try
            {
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 0)
                    File.Copy(FilePath, BakPath, overwrite: true);
            }
            catch { }
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Copy(tmp, FilePath, overwrite: true);
            try { File.Delete(tmp); } catch { }
            // 来源非空时同步加固 .bak
            if (ImageFolders.Count > 0)
            {
                try { File.WriteAllText(BakPath, json); } catch { }
            }
        }
        catch { }
    }

    /// <summary>解析 imgtag 数据库路径：程序目录 imgTag\db.sqlite 优先（并入程序包）→ 显式设置 → 程序目录默认</summary>
    public static string ResolveImgTagDb(string? configured)
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "imgtag", "db.sqlite");
        if (File.Exists(bundled)) return bundled;
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        return bundled;
    }

    /// <summary>解析 imgtag 运行目录（数据目录：db/models/onnxruntime.dll/imgtag_native.dll 所在处）</summary>
    public static string ResolveImgTagDir(string dbPath)
        => Path.GetDirectoryName(dbPath) ?? AppContext.BaseDirectory;

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "PictureButler";

    /// <summary>开机自启（注册表 HKCU Run）。以 enable 为准写入或删除，保证注册表与设置一致。</summary>
    public static bool SetAutoStart(bool enable)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            if (key == null) return false;
            if (enable)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return false;
                key.SetValue(RunValueName, $"\"{exe}\"");
            }
            else
            {
                // false 时必须删干净；DeleteValue 第二参 false 表示值不存在也不抛
                key.DeleteValue(RunValueName, false);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            return key?.GetValue(RunValueName) != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>启动时以设置为准同步注册表：设置关则删 Run 项，设置开则确保 Run 项存在（修复 UI 未勾选仍开机自启）。</summary>
    public static void SyncAutoStartWithSettings(bool autoStartSetting)
        => SetAutoStart(autoStartSetting);
}
