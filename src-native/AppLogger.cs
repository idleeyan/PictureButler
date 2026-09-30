using System.IO;
using System.Text;

namespace PictureButler;

/// <summary>
/// 统一日志：写到 %APPDATA%\com.picturebutler.app\logs\（不写程序目录，符合 0.61.1 洁净约定）。
/// Release 保留 INFO/WARN/ERROR；按天滚动；默认保留 14 天。
/// </summary>
public static class AppLogger
{
    private static readonly object _lock = new();
    private const int KeepDays = 14;

    public static string LogDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "com.picturebutler.app", "logs");

    private static string TodayFile => Path.Combine(LogDir, $"pb_{DateTime.Now:yyyyMMdd}.log");

    static AppLogger()
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            CleanupOld();
        }
        catch { }
    }

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg) => Write("ERROR", msg);
    public static void Error(string msg, Exception ex) => Write("ERROR", msg + "\n" + ex);

    private static void Write(string level, string msg)
    {
        try
        {
            var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {msg}{Environment.NewLine}";
            lock (_lock)
            {
                File.AppendAllText(TodayFile, line, Encoding.UTF8);
            }
        }
        catch { /* 日志绝不能把程序拖垮 */ }
    }

    private static void CleanupOld()
    {
        try
        {
            if (!Directory.Exists(LogDir)) return;
            var cutoff = DateTime.Now.AddDays(-KeepDays);
            foreach (var f in Directory.GetFiles(LogDir, "pb_*.log"))
            {
                try
                {
                    if (File.GetLastWriteTime(f) < cutoff) File.Delete(f);
                }
                catch { }
            }
        }
        catch { }
    }
}
