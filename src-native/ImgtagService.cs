using System.IO;
using Microsoft.Data.Sqlite;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PictureButler;

/// <summary>imgtag 图片库服务：只读 imgtag db.sqlite（图片为本地文件路径）</summary>
public class ImgtagService
{
    private readonly string _dbPath;

    public ImgtagService(string dbPath)
    {
        _dbPath = dbPath;
    }

    /// <summary>验证库路径是否可用</summary>
    public static string? TestPath(string dbPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
                return "文件不存在";
            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM images";
            cmd.ExecuteScalar();
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public int Count()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM images";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public List<ImgItem> Page(int offset, int limit)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, path, filename, description, tags, width, height, file_size, faces_scanned FROM images ORDER BY id DESC LIMIT $l OFFSET $o";
        cmd.Parameters.AddWithValue("$l", limit);
        cmd.Parameters.AddWithValue("$o", offset);
        return ReadItems(cmd);
    }

    public List<ImgItem> Search(string q, int offset, int limit)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT images.id, images.path, images.filename, images.description, images.tags,
                   images.width, images.height, images.file_size, images.faces_scanned
            FROM images_fts
            JOIN images ON images.id = images_fts.rowid
            WHERE images_fts MATCH $q
            ORDER BY rank
            LIMIT $l OFFSET $o
            """;
        cmd.Parameters.AddWithValue("$q", q);
        cmd.Parameters.AddWithValue("$l", limit);
        cmd.Parameters.AddWithValue("$o", offset);
        return ReadItems(cmd);
    }

    public int SearchCount(string q)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM images_fts WHERE images_fts MATCH $q";
        cmd.Parameters.AddWithValue("$q", q);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static List<ImgItem> ReadItems(SqliteCommand cmd)
    {
        var list = new List<ImgItem>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new ImgItem
            {
                Id = r.GetInt32(0),
                Path = r.GetString(1),
                Filename = r.GetString(2),
                Description = r.IsDBNull(3) ? "" : r.GetString(3),
                Tags = r.IsDBNull(4) ? "" : r.GetString(4),
                Width = r.IsDBNull(5) ? 0 : r.GetInt32(5),
                Height = r.IsDBNull(6) ? 0 : r.GetInt32(6),
                FileSize = r.IsDBNull(7) ? 0 : r.GetInt64(7),
                FaceScanned = !r.IsDBNull(8) && r.GetInt32(8) > 0
            });
        }
        return list;
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly");
        conn.Open();
        return conn;
    }
}

public class ImgItem : System.ComponentModel.INotifyPropertyChanged
{
    public int Id { get; set; }
    public string Path { get; set; } = "";
    public string Filename { get; set; } = "";
    public string Description { get; set; } = "";
    public string Tags { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public long FileSize { get; set; }
    public bool FaceScanned { get; set; }
    public string Source { get; set; } = "imgtag";   // folder / imgtag

    /// <summary>是否已 AI 打标（有描述或标签）</summary>
    public bool HasAiTag => !string.IsNullOrEmpty(Description) || !string.IsNullOrEmpty(Tags);

    /// <summary>是否有相关提示词（ComfyUI 生成图片文件名与提示词 generationInfo 匹配）</summary>
    public bool HasPrompt { get; set; }

    private bool _selected;
    /// <summary>多选选中态（数据驱动，虚拟化回收容器后仍正确）</summary>
    public bool Selected
    {
        get => _selected;
        set { if (_selected != value) { _selected = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Selected))); } }
    }

    private ImageSource? _thumb;
    [System.Text.Json.Serialization.JsonIgnore]
    public ImageSource? Thumb
    {
        get => _thumb;
        set { _thumb = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Thumb))); }
    }

    public string SizeDisplay => Width > 0 && Height > 0 ? $"{Width}×{Height}" : "";
    public string FileSizeDisplay => FileSize > 0 ? $"{FileSize / 1024.0 / 1024.0:F1}MB" : "";

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}
