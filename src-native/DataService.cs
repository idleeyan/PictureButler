using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace PictureButler;

/// <summary>
/// 数据服务：直接读取现有 prompts.db（Tauri 版同库共读，零迁移）。
/// - 元数据：kv.items_chunk_N（每块是独立 JSON 数组），itemsChunkCount 记录块数
/// - 图片：img_{id}_{kind}_chunk_N 分块 + itemImgMeta_{id} 元数据
/// 929MB 库中绝大多数是图片分块，元数据仅 ~230KB，列表加载极快。
/// </summary>
public class DataService
{
    private readonly string _dbPath;

    public DataService(string dbPath)
    {
        _dbPath = dbPath;
    }

    private SqliteConnection Open(bool readOnly = true)
    {
        var mode = readOnly ? "Mode=ReadOnly" : "";
        var conn = new SqliteConnection($"Data Source={_dbPath};{mode}");
        conn.Open();
        return conn;
    }

    private static string? ReadValue(SqliteConnection conn, string key)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM kv WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>加载全部提示词（元数据，不含图片）</summary>
    public List<PromptItem> LoadItems()
    {
        using var conn = Open();
        int chunkCount = 0;
        var cntStr = ReadValue(conn, "itemsChunkCount");
        if (int.TryParse(cntStr, out var c)) chunkCount = c;

        var result = new List<PromptItem>();
        if (chunkCount == 0)
        {
            // 旧版单 key 格式兜底
            var legacy = ReadValue(conn, "items");
            if (!string.IsNullOrEmpty(legacy))
            {
                var arr = Deserialize(legacy);
                if (arr != null) result.AddRange(arr);
            }
            return result;
        }

        var opts = JsonOpts();
        for (int i = 0; i < chunkCount; i++)
        {
            var json = ReadValue(conn, $"items_chunk_{i}");
            if (string.IsNullOrEmpty(json)) continue;
            try
            {
                var arr = JsonSerializer.Deserialize<List<PromptItem>>(json, opts);
                if (arr != null) result.AddRange(arr);
            }
            catch (JsonException)
            {
                // 单块损坏不影响其它块
            }
        }
        return result;
    }

    /// <summary>按 id 加载预览图 dataUrl（无则返回空串）</summary>
    public string LoadPreviewImage(string id)
    {
        using var conn = Open();
        return LoadImageKind(conn, id, "preview");
    }

    /// <summary>批量移除 gallery 图片（按原始 index），一次性删除块并更新 meta（保留其他项的 index 不变）</summary>
    public void ApplyGalleryRemovals(string id, IEnumerable<int> removedIndexes)
    {
        var set = new HashSet<int>(removedIndexes);
        if (set.Count == 0) return;
        using var conn = Open(readOnly: false);
        foreach (var idx in set)
        {
            var del = conn.CreateCommand();
            del.CommandText = "DELETE FROM kv WHERE key LIKE $p";
            del.Parameters.AddWithValue("$p", $"img_{id}_gallery_{idx}_chunk_%");
            del.ExecuteNonQuery();
        }
        // 更新 meta：gallery 移除对应项
        var metaJson = ReadValue(conn, $"itemImgMeta_{id}");
        var meta = new Dictionary<string, object?>();
        if (!string.IsNullOrEmpty(metaJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(metaJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    foreach (var prop in doc.RootElement.EnumerateObject())
                        meta[prop.Name] = prop.Value.Clone();
            }
            catch { }
        }
        if (meta.ContainsKey("gallery") && meta["gallery"] is JsonElement ge && ge.ValueKind == JsonValueKind.Array)
        {
            var items = new List<object?>();
            foreach (var item in ge.EnumerateArray())
            {
                int idx = -1;
                if (item.TryGetProperty("index", out var ix)) idx = ix.GetInt32();
                if (idx >= 0 && set.Contains(idx)) continue;
                items.Add(item.Clone());
            }
            meta["gallery"] = items;
        }
        WriteMeta(conn, id, meta);
    }

    /// <summary>移除预览图（封面），保留 gallery</summary>
    public void RemovePreviewImage(string id)
    {
        using var conn = Open(readOnly: false);
        var del = conn.CreateCommand();
        del.CommandText = "DELETE FROM kv WHERE key LIKE $p";
        del.Parameters.AddWithValue("$p", $"img_{id}_preview_chunk_%");
        del.ExecuteNonQuery();

        var metaJson = ReadValue(conn, $"itemImgMeta_{id}");
        var meta = new Dictionary<string, object?>();
        if (!string.IsNullOrEmpty(metaJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(metaJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    foreach (var prop in doc.RootElement.EnumerateObject())
                        meta[prop.Name] = prop.Value.Clone(); // 保留原值
            }
            catch { }
        }
        meta["preview"] = new Dictionary<string, int> { ["chunkCount"] = 0 };
        meta["hasPreview"] = false;
        if (!meta.ContainsKey("gallery")) meta["gallery"] = new List<object>();
        if (!meta.ContainsKey("images")) meta["images"] = new List<object>();
        WriteMeta(conn, id, meta);
    }

    /// <summary>保存预览图（dataUrl 分块写入，与旧版 itemImgMeta_ 格式兼容）</summary>
    public void SavePreviewImage(string id, string dataUrl)
    {
        using var conn = Open(readOnly: false);
        SaveImageChunks(conn, id, "preview", dataUrl);
        // 更新 meta：保留原有 gallery/images 字段，设置 preview
        var metaJson = ReadValue(conn, $"itemImgMeta_{id}");
        using var metaDoc = string.IsNullOrEmpty(metaJson) ? null : JsonDocument.Parse(metaJson);
        var meta = new Dictionary<string, object?>();
        if (metaDoc != null && metaDoc.RootElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in metaDoc.RootElement.EnumerateObject())
                meta[prop.Name] = prop.Value.Clone(); // 保留原值（gallery/images 等）
        }
        int count = CountChunks(conn, id, "preview");
        meta["preview"] = new Dictionary<string, int> { ["chunkCount"] = count };
        meta["hasPreview"] = true;
        meta["imageCount"] = 0;
        if (!meta.ContainsKey("gallery")) meta["gallery"] = new List<object>();
        if (!meta.ContainsKey("images")) meta["images"] = new List<object>();
        WriteMeta(conn, id, meta);
    }

    /// <summary>保存多图：第一张作为 preview，其余进 gallery（与旧版格式兼容）</summary>
    public void SaveImages(string id, List<string> dataUrls)
    {
        using var conn = Open(readOnly: false);
        var meta = new Dictionary<string, object?>
        {
            ["gallery"] = new List<object>(),
            ["images"] = new List<object>(),
            ["imageCount"] = dataUrls.Count,
            ["hasPreview"] = dataUrls.Count > 0
        };
        if (dataUrls.Count > 0)
        {
            SaveImageChunks(conn, id, "preview", dataUrls[0]);
            meta["preview"] = new Dictionary<string, int> { ["chunkCount"] = CountChunks(conn, id, "preview") };
        }
        var gallery = new List<object>();
        for (int i = 0; i < dataUrls.Count; i++)
        {
            SaveImageChunks(conn, id, $"gallery_{i}", dataUrls[i]);
            gallery.Add(new Dictionary<string, int>
            {
                ["chunkCount"] = CountChunks(conn, id, $"gallery_{i}"),
                ["index"] = i
            });
        }
        meta["gallery"] = gallery;
        WriteMeta(conn, id, meta);
    }

    private void SaveImageChunks(SqliteConnection conn, string id, string kind, string dataUrl)
    {
        // 先删除旧块，再按 ~4000 字符分块写入（每块 JSON 转义后单独存 kv）
        var del = conn.CreateCommand();
        del.CommandText = "DELETE FROM kv WHERE key LIKE $p";
        del.Parameters.AddWithValue("$p", $"img_{id}_{kind}_chunk_%");
        del.ExecuteNonQuery();

        const int chunkLen = 4000;
        int i = 0;
        for (int pos = 0; pos < dataUrl.Length; pos += chunkLen, i++)
        {
            var part = dataUrl.Substring(pos, Math.Min(chunkLen, dataUrl.Length - pos));
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO kv(key, value, updated_at) VALUES($k, $v, $t)";
            cmd.Parameters.AddWithValue("$k", $"img_{id}_{kind}_chunk_{i}");
            cmd.Parameters.AddWithValue("$v", JsonSerializer.Serialize(part));
            cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            cmd.ExecuteNonQuery();
        }
    }

    private static int CountChunks(SqliteConnection conn, string id, string kind)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM kv WHERE key LIKE $p";
        cmd.Parameters.AddWithValue("$p", $"img_{id}_{kind}_chunk_%");
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static void WriteMeta(SqliteConnection conn, string id, Dictionary<string, object?> meta)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE kv SET value = $v, updated_at = $t WHERE key = $k";
        cmd.Parameters.AddWithValue("$v", JsonSerializer.Serialize(meta));
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$k", $"itemImgMeta_{id}");
        var n = cmd.ExecuteNonQuery();
        if (n == 0)
        {
            using var ins = conn.CreateCommand();
            ins.CommandText = "INSERT INTO kv(key, value, updated_at) VALUES($k, $v, $t)";
            ins.Parameters.AddWithValue("$k", $"itemImgMeta_{id}");
            ins.Parameters.AddWithValue("$v", JsonSerializer.Serialize(meta));
            ins.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            ins.ExecuteNonQuery();
        }
    }

    /// <summary>按 id 加载 gallery 图片列表（含原始 index）</summary>
    public List<(int Index, string DataUrl)> LoadGalleryItems(string id)
    {
        using var conn = Open();
        var result = new List<(int, string)>();
        var meta = ReadValue(conn, $"itemImgMeta_{id}");
        if (string.IsNullOrEmpty(meta)) return result;
        try
        {
            using var doc = JsonDocument.Parse(meta);
            // 元数据结构：{"gallery":[{"chunkCount":392,"index":0,...}],"preview":{"chunkCount":392}}
            if (doc.RootElement.TryGetProperty("gallery", out var g) && g.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in g.EnumerateArray())
                {
                    int idx = item.TryGetProperty("index", out var ix) ? ix.GetInt32() : 0;
                    int cc = item.TryGetProperty("chunkCount", out var c2) ? c2.GetInt32() : 0;
                    var url = JoinChunks(conn, id, $"gallery_{idx}", cc);
                    if (!string.IsNullOrEmpty(url)) result.Add((idx, url));
                }
            }
        }
        catch (JsonException) { }
        return result;
    }

    /// <summary>按 id 加载 gallery 图片列表</summary>
    public List<string> LoadGalleryImages(string id)
    {
        using var conn = Open();
        var result = new List<string>();
        var meta = ReadValue(conn, $"itemImgMeta_{id}");
        if (string.IsNullOrEmpty(meta)) return result;
        try
        {
            using var doc = JsonDocument.Parse(meta);
            // 元数据结构：{"gallery":[{"chunkCount":392,"index":0,...}],"preview":{"chunkCount":392}}
            if (doc.RootElement.TryGetProperty("gallery", out var g) && g.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in g.EnumerateArray())
                {
                    int idx = item.TryGetProperty("index", out var ix) ? ix.GetInt32() : 0;
                    int cc = item.TryGetProperty("chunkCount", out var c2) ? c2.GetInt32() : 0;
                    var url = JoinChunks(conn, id, $"gallery_{idx}", cc);
                    if (!string.IsNullOrEmpty(url)) result.Add(url);
                }
            }
        }
        catch (JsonException) { }
        return result;
    }

    private string LoadImageKind(SqliteConnection conn, string id, string kind)
    {
        var meta = ReadValue(conn, $"itemImgMeta_{id}");
        if (string.IsNullOrEmpty(meta)) return "";
        try
        {
            using var doc = JsonDocument.Parse(meta);
            if (doc.RootElement.TryGetProperty(kind, out var k))
            {
                if (k.TryGetProperty("chunkCount", out var cc))
                {
                    return JoinChunks(conn, id, kind, cc.GetInt32());
                }
                if (k.TryGetProperty("dataUrl", out var du)) return du.GetString() ?? "";
            }
        }
        catch (JsonException) { }
        return "";
    }

    private static string JoinChunks(SqliteConnection conn, string id, string kind, int chunkCount)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < chunkCount; i++)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM kv WHERE key = $k";
            cmd.Parameters.AddWithValue("$k", $"img_{id}_{kind}_chunk_{i}");
            var v = cmd.ExecuteScalar() as string;
            if (string.IsNullOrEmpty(v)) continue;
            // 每块是 JSON 编码的字符串（带引号转义），需反序列化还原
            try { sb.Append(JsonSerializer.Deserialize<string>(v)); }
            catch { sb.Append(v); } // 兼容非转义旧数据
        }
        return sb.ToString();
    }

    /// <summary>切换星标并持久化（修改对应 chunk 中的条目）</summary>
    public void ToggleStar(string id)
    {
        using var conn = Open(readOnly: false);
        int chunkCount = int.TryParse(ReadValue(conn, "itemsChunkCount"), out var c) ? c : 0;
        for (int i = 0; i < chunkCount; i++)
        {
            var json = ReadValue(conn, $"items_chunk_{i}");
            if (string.IsNullOrEmpty(json)) continue;
            var arr = Deserialize(json);
            if (arr == null) continue;
            var item = arr.FirstOrDefault(x => x.Id == id);
            if (item == null) continue;
            item.Starred = !item.Starred;
            Touch(item);
            WriteChunk(conn, i, arr);
            break;
        }
    }

    /// <summary>新建或更新提示词（更新：定位所在 chunk 改写；新建：插入 chunk0 最前）</summary>
    public void SaveItem(PromptItem item, bool isNew)
    {
        using var conn = Open(readOnly: false);
        int chunkCount = int.TryParse(ReadValue(conn, "itemsChunkCount"), out var c) ? c : 0;
        if (chunkCount == 0)
        {
            // 兜底：无分块库
            var legacy = ReadValue(conn, "items");
            var arr = string.IsNullOrEmpty(legacy) ? new List<PromptItem>() : Deserialize(legacy) ?? new();
            if (isNew) arr.Insert(0, item); else { var i = arr.FindIndex(x => x.Id == item.Id); if (i >= 0) arr[i] = item; }
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE kv SET value = $v, updated_at = $t WHERE key = 'items'";
            cmd.Parameters.AddWithValue("$v", JsonSerializer.Serialize(arr, JsonOpts()));
            cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            cmd.ExecuteNonQuery();
            return;
        }

        if (!isNew)
        {
            for (int i = 0; i < chunkCount; i++)
            {
                var json = ReadValue(conn, $"items_chunk_{i}");
                if (string.IsNullOrEmpty(json)) continue;
                var arr = Deserialize(json);
                if (arr == null) continue;
                var idx = arr.FindIndex(x => x.Id == item.Id);
                if (idx < 0) continue;
                arr[idx] = item;
                Touch(item);
                WriteChunk(conn, i, arr);
                return;
            }
            // 未找到 → 视为新建追加
        }

        // 新建：插入 chunk0 最前
        var json0 = ReadValue(conn, "items_chunk_0");
        var arr0 = Deserialize(json0) ?? new List<PromptItem>();
        item.CreatedAt ??= DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        Touch(item);
        arr0.Insert(0, item);
        WriteChunk(conn, 0, arr0);
    }

    /// <summary>删除提示词：从 chunk 移除并记录到 deletedItems（与旧版同步格式兼容）</summary>
    public void DeleteItem(PromptItem item)
    {
        using var conn = Open(readOnly: false);
        int chunkCount = int.TryParse(ReadValue(conn, "itemsChunkCount"), out var c) ? c : 0;
        for (int i = 0; i < chunkCount; i++)
        {
            var json = ReadValue(conn, $"items_chunk_{i}");
            if (string.IsNullOrEmpty(json)) continue;
            var arr = Deserialize(json);
            if (arr == null) continue;
            var idx = arr.FindIndex(x => x.Id == item.Id);
            if (idx < 0) continue;
            arr.RemoveAt(idx);
            WriteChunk(conn, i, arr);
            break;
        }

        // 记录删除（deletedItems 与旧版格式一致：{id,type,version,deletedAt,checksum}）
        var delJson = ReadValue(conn, "deletedItems");
        var delList = new List<Dictionary<string, object?>>();
        if (!string.IsNullOrEmpty(delJson))
        {
            try { delList = JsonSerializer.Deserialize<List<Dictionary<string, object?>>>(delJson) ?? new(); }
            catch { }
        }
        delList.Add(new Dictionary<string, object?>
        {
            ["id"] = item.Id,
            ["type"] = item.Type ?? "prompt",
            ["version"] = (item.Version ?? 1) + 1,
            ["deletedAt"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
            ["checksum"] = item.Checksum ?? ""
        });
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE kv SET value = $v, updated_at = $t WHERE key = 'deletedItems'";
        cmd.Parameters.AddWithValue("$v", JsonSerializer.Serialize(delList, JsonOpts()));
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.ExecuteNonQuery();
    }

    private static void Touch(PromptItem item)
    {
        item.Version = (item.Version ?? 1) + 1;
        item.UpdatedAt = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
    }

    private static void WriteChunk(SqliteConnection conn, int chunkIndex, List<PromptItem> arr)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE kv SET value = $v, updated_at = $t WHERE key = $k";
        cmd.Parameters.AddWithValue("$v", JsonSerializer.Serialize(arr, JsonOpts()));
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$k", $"items_chunk_{chunkIndex}");
        cmd.ExecuteNonQuery();
    }

    /// <summary>生成旧版兼容的新 id（22 位小写字母数字）</summary>
    public static string NewId()
    {
        const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
        var rnd = Random.Shared;
        return new string(Enumerable.Range(0, 22).Select(_ => chars[rnd.Next(chars.Length)]).ToArray());
    }

    private static List<PromptItem>? Deserialize(string json)
        => JsonSerializer.Deserialize<List<PromptItem>>(json, JsonOpts());

    private static JsonSerializerOptions JsonOpts()
    {
        var opts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        return opts;
    }
}

/// <summary>提示词条目（与 prompts.db items JSON 字段对齐）</summary>
public class PromptItem : System.ComponentModel.INotifyPropertyChanged
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("content")] public string Content { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "prompt";
    [JsonPropertyName("starred")] public bool Starred { get; set; }
    [JsonPropertyName("tags")] public List<string>? Tags { get; set; }
    [JsonPropertyName("category")] public string? Category { get; set; }
    [JsonPropertyName("createdAt")] public string? CreatedAt { get; set; }
    [JsonPropertyName("updatedAt")] public string? UpdatedAt { get; set; }
    [JsonPropertyName("version")] public int? Version { get; set; }
    [JsonPropertyName("checksum")] public string? Checksum { get; set; }
    [JsonPropertyName("generationInfo")] public object? GenerationInfo { get; set; }
    [JsonPropertyName("_hasPreviewImage")] public bool? HasPreviewImage { get; set; }
    [JsonPropertyName("_hasGalleryImages")] public bool? HasGalleryImages { get; set; }

    /// <summary>编辑保存时标记：同时删除该提示词（卡片图片删空后的联动）</summary>
    [JsonIgnore] public bool ShouldDelete { get; set; }

    /// <summary>批量多选标记（不落库）</summary>
    [JsonIgnore] private bool _selected;
    [JsonIgnore] public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Selected)));
        }
    }

    [JsonIgnore] public string UpdatedDisplay => FormatDate(UpdatedAt);

    // 卡片缩略图（后台解码后经 Dispatcher 赋值，绑定自动刷新）
    [JsonIgnore] private System.Windows.Media.ImageSource? _previewImage;
    [JsonIgnore] public System.Windows.Media.ImageSource? PreviewImage
    {
        get => _previewImage;
        set
        {
            if (ReferenceEquals(_previewImage, value)) return;
            _previewImage = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(PreviewImage)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private static string FormatDate(string? iso)
    {
        if (string.IsNullOrEmpty(iso)) return "";
        try
        {
            var dt = DateTime.Parse(iso).ToLocalTime();
            return dt.ToString("yyyy-MM-dd HH:mm");
        }
        catch
        {
            return iso.Length > 16 ? iso[..16] : iso;
        }
    }
}
