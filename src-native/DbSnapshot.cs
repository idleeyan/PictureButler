using System.IO;
using Microsoft.Data.Sqlite;

namespace PictureButler;

/// <summary>
/// 关键数据库的按天滚动快照（0.62.7 / 0.62.9）。
/// 防的是「程序逻辑误删/误清」——0.62.3 的 image_index.db 整页消失就是这类事故。
/// 只快照小库（image_index.db ~1MB）；prompts.db 体积大不做自动快照。
/// </summary>
public static class DbSnapshot
{
    private const int KeepDays = 7;

    /// <summary>
    /// 每天一份快照到 backups\，保留 7 天。同日重复调用不覆盖
    /// （保留当天最早的那份，更接近「误操作前」的状态）。
    /// 用 <see cref="SqliteConnection.BackupDatabase"/> 做在线备份，WAL/并发写也安全。
    /// </summary>
    public static void SnapshotIfChanged(string dbPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath)) return;

            var dir = Path.GetDirectoryName(dbPath);
            if (string.IsNullOrEmpty(dir)) return;
            var backupDir = Path.Combine(dir, "backups");
            Directory.CreateDirectory(backupDir);

            var baseName = Path.GetFileName(dbPath);
            var today = DateTime.Now.ToString("yyyyMMdd");
            var todayBak = Path.Combine(backupDir, $"{baseName}.{today}.bak");

            if (File.Exists(todayBak)) { CleanupOld(backupDir, baseName); return; }

            // SQLite 在线备份 API：即使源库有 WAL / 正在写，也能拿到一致快照
            using (var src = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly"))
            {
                src.Open();
                using var dst = new SqliteConnection($"Data Source={todayBak}");
                dst.Open();
                src.BackupDatabase(dst);
            }

            AppLogger.Info($"DbSnapshot: {baseName} -> {Path.GetFileName(todayBak)} ({new FileInfo(todayBak).Length} bytes)");
            CleanupOld(backupDir, baseName);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"DbSnapshot failed for {dbPath}: {ex.Message}");
        }
    }

    private static void CleanupOld(string backupDir, string baseName)
    {
        try
        {
            var cutoff = DateTime.Now.Date.AddDays(-KeepDays);
            foreach (var f in Directory.GetFiles(backupDir, $"{baseName}.*.bak"))
            {
                var name = Path.GetFileName(f);
                // 形如 xxx.db.20260930.bak
                var parts = name.Split('.');
                if (parts.Length < 3) continue;
                var datePart = parts[^2];
                if (DateTime.TryParseExact(datePart, "yyyyMMdd", null,
                        System.Globalization.DateTimeStyles.None, out var d)
                    && d < cutoff)
                {
                    try { File.Delete(f); AppLogger.Info($"DbSnapshot: removed old {name}"); } catch { }
                }
            }
        }
        catch { }
    }
}
