using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;

namespace PictureButler;

/// <summary>
/// 整图缩略图磁盘缓存：key 由 路径+修改时间+大小+解码宽度 决定，文件改动自动失效。
/// 人脸封面走 imgtag 的 .faces，不进这里；只有自建索引/整图缩略走本缓存。
/// 缓存放 %LOCALAPPDATA%\com.picturebutler.app\thumbs，二次进同一页近瞬出图、不重复解码原图。
/// </summary>
public static class ThumbCache
{
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "com.picturebutler.app", "thumbs");

    static ThumbCache()
    {
        try { Directory.CreateDirectory(CacheDir); } catch { }
    }

    private static string KeyOf(string path, long mtimeTicks, long size, int decodeWidth)
    {
        var raw = Encoding.UTF8.GetBytes($"{path}|{mtimeTicks}|{size}|{decodeWidth}");
        var hash = SHA1.HashData(raw);
        return Convert.ToHexString(hash);
    }

    /// <summary>按指定宽度解码图片（OnLoad + Freeze，可跨线程）</summary>
    public static BitmapImage? Decode(string path, int decodeWidth)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.DecodePixelWidth = decodeWidth;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    /// <summary>命中缓存直接读小图；未命中解码原图并回写缓存。hit 标记是否命中（用于日志统计）。</summary>
    public static BitmapImage? GetOrCreate(string path, int decodeWidth, out bool hit)
    {
        hit = false;
        if (!File.Exists(path)) return null;
        try
        {
            var fi = new FileInfo(path);
            var key = KeyOf(path, fi.LastWriteTimeUtc.Ticks, fi.Length, decodeWidth);
            var cacheFile = Path.Combine(CacheDir, key + ".jpg");
            if (File.Exists(cacheFile))
            {
                hit = true;
                return Decode(cacheFile, decodeWidth);
            }
            var bmp = Decode(path, decodeWidth);
            if (bmp == null) return null;
            TrySaveJpeg(bmp, cacheFile);
            return bmp;
        }
        catch { return null; }
    }

    private static void TrySaveJpeg(BitmapSource source, string file)
    {
        try
        {
            var tmp = file + ".tmp";
            using (var fs = File.Create(tmp))
            {
                var enc = new JpegBitmapEncoder { QualityLevel = 88 };
                enc.Frames.Add(BitmapFrame.Create(source));
                enc.Save(fs);
            }
            if (File.Exists(file)) File.Delete(file);
            File.Move(tmp, file);
        }
        catch { try { if (File.Exists(file + ".tmp")) File.Delete(file + ".tmp"); } catch { } }
    }

    /// <summary>缓存统计：文件数与总字节</summary>
    public static (int count, long bytes) Stats()
    {
        try
        {
            if (!Directory.Exists(CacheDir)) return (0, 0);
            int n = 0; long bytes = 0;
            foreach (var f in Directory.EnumerateFiles(CacheDir, "*.jpg"))
            {
                try { var fi = new FileInfo(f); n++; bytes += fi.Length; } catch { }
            }
            return (n, bytes);
        }
        catch { return (0, 0); }
    }

    /// <summary>清空缓存，返回删除的文件数</summary>
    public static int Clear()
    {
        try
        {
            if (!Directory.Exists(CacheDir)) return 0;
            int n = 0;
            foreach (var f in Directory.EnumerateFiles(CacheDir, "*.jpg"))
            {
                try { File.Delete(f); n++; } catch { }
            }
            return n;
        }
        catch { return 0; }
    }
}
