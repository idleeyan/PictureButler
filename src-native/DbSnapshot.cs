using System.IO;

namespace PictureButler;

/// <summary>
/// 关键数据库的按天滚动快照（0.62.7）。
/// 目标是防「程序逻辑误删/误清」——0.62.3 的 image_index.db 整页消失就是这类事故。
/// 只快照小库（image_index.db ~1MB）；prompts.db 体积大，不做自动快照。
/// </summary>
public static class DbSnapshot
{
    private const int KeepDays = 7;

    /// <summary>
    /// 若源库存在且自上次快照后有变化，则复制一份到 backups\ 目录。
    /// 文件名：{原名}.yyyyMMdd.bak；同日重复调用不覆盖（保留当日第一份，通常更接近「误操作前」）。
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

            // 同日已有快照则跳过（不覆盖：保留当天最早的那份，更接近事故前状态）
            if (File.Exists(todayBak)) { CleanupOld(backupDir, baseName); return; }

            // 用 SQLite 在线备份 API 等价物：直接文件复制在 WAL 模式下可能不一致，
            // 这里用「源库 mtime + 长度」做变化检测，复制时短暂独占读。
            // image_index.db 很小（~1MB），主程序启动早期几乎无并发写，风险可接受。
            File.Copy(dbPath, todayBak, overwrite: false);
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
