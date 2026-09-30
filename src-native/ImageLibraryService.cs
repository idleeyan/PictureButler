using System.IO;
using Microsoft.Data.Sqlite;

namespace PictureButler;

/// <summary>图片库统一服务：自建文件夹索引（image_index.db）∪ imgtag 库（只读 ATTACH）</summary>
public class ImageLibraryService
{
    private readonly string _indexDbPath;
    private readonly string? _imgtagDbPath;

    /// <summary>
    /// 允许出现在图库中的来源根目录（设置页已添加的图片文件夹）。
    /// null = 不限制；空列表 = 不显示任何图；非空 = 仅显示路径位于这些根目录下的图（只过滤展示，不删磁盘文件）。
    /// </summary>
    public List<string>? AllowedRoots { get; set; }

    private static readonly string[] ImageExts = { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".jfif", ".avif" };

    public ImageLibraryService(string indexDbPath, string? imgtagDbPath)
    {
        _indexDbPath = indexDbPath;
        _imgtagDbPath = imgtagDbPath;
        InitDb();
    }

    private void InitDb()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_indexDbPath)!);
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS images (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                path TEXT UNIQUE NOT NULL,
                filename TEXT NOT NULL,
                folder TEXT NOT NULL,
                width INTEGER NOT NULL DEFAULT 0,
                height INTEGER NOT NULL DEFAULT 0,
                file_size INTEGER NOT NULL DEFAULT 0,
                mtime INTEGER NOT NULL DEFAULT 0,
                added_at INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_images_filename ON images(filename);
            """;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={_indexDbPath}");
        conn.Open();
        if (!string.IsNullOrWhiteSpace(_imgtagDbPath) && File.Exists(_imgtagDbPath))
        {
            try
            {
                using var attach = conn.CreateCommand();
                attach.CommandText = "ATTACH DATABASE $p AS it";
                attach.Parameters.AddWithValue("$p", _imgtagDbPath);
                attach.ExecuteNonQuery();
            }
            catch { }
        }
        return conn;
    }

    private bool HasIt(SqliteConnection conn)
    {
        if (!HasImgTagData) return false;
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM it.images LIMIT 1";
            cmd.ExecuteScalar();
            return true;
        }
        catch { return false; }
    }

    /// <summary>扫描文件夹（增量：mtime+size 变化才更新；消失的文件移除），返回新增数</summary>
    public async Task<int> ScanFoldersAsync(IEnumerable<string> folders, IProgress<string>? progress = null)
    {
        var list = folders.Where(Directory.Exists).ToList();
        var all = new List<(string Path, string Folder, long Size, long Mtime)>();
        await Task.Run(() =>
        {
            foreach (var folder in list)
            {
                var n = 0;
                foreach (var f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                {
                    if (!ImageExts.Contains(Path.GetExtension(f).ToLowerInvariant())) continue;
                    try
                    {
                        var fi = new FileInfo(f);
                        all.Add((f, folder, fi.Length, new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds()));
                    }
                    catch { }
                    if (++n % 200 == 0)
                        progress?.Report($"扫描中… {folder} ({n})");
                }
            }
        });

        // 写库：存在则更新 mtime/size，新增插入
        using var conn = Open();
        int added = 0, updated = 0;
        using (var tx = conn.BeginTransaction())
        {
            var upd = conn.CreateCommand();
            upd.CommandText = "UPDATE images SET mtime=$m, file_size=$s, filename=$f, folder=$d WHERE id=$i";
            upd.Parameters.Add("$m", SqliteType.Integer);
            upd.Parameters.Add("$s", SqliteType.Integer);
            upd.Parameters.Add("$f", SqliteType.Text);
            upd.Parameters.Add("$d", SqliteType.Text);
            upd.Parameters.Add("$i", SqliteType.Integer);

            var ins = conn.CreateCommand();
            ins.CommandText = "INSERT OR IGNORE INTO images (path, filename, folder, width, height, file_size, mtime, added_at) VALUES ($p,$f,$d,$w,$h,$s,$m,$a)";
            ins.Parameters.Add("$p", SqliteType.Text);
            ins.Parameters.Add("$f", SqliteType.Text);
            ins.Parameters.Add("$d", SqliteType.Text);
            ins.Parameters.Add("$w", SqliteType.Integer);
            ins.Parameters.Add("$h", SqliteType.Integer);
            ins.Parameters.Add("$s", SqliteType.Integer);
            ins.Parameters.Add("$m", SqliteType.Integer);
            ins.Parameters.Add("$a", SqliteType.Integer);

            var sel = conn.CreateCommand();
            sel.CommandText = "SELECT id, mtime, file_size FROM images WHERE path = $p";
            sel.Parameters.Add("$p", SqliteType.Text);

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var (p, folder, size, mtime) in all)
            {
                sel.Parameters["$p"].Value = p;
                long id = -1, oldM = 0, oldS = 0;
                using (var r = sel.ExecuteReader())
                {
                    if (r.Read())
                    {
                        id = r.GetInt64(0);
                        oldM = r.GetInt64(1);
                        oldS = r.GetInt64(2);
                    }
                }
                if (id >= 0)
                {
                    if (oldM != mtime || oldS != size)
                    {
                        upd.Parameters["$m"].Value = mtime;
                        upd.Parameters["$s"].Value = size;
                        upd.Parameters["$f"].Value = Path.GetFileName(p);
                        upd.Parameters["$d"].Value = folder;
                        upd.Parameters["$i"].Value = id;
                        upd.ExecuteNonQuery();
                        updated++;
                    }
                }
                else
                {
                    var (w, h) = FastImageSize(p);
                    ins.Parameters["$p"].Value = p;
                    ins.Parameters["$f"].Value = Path.GetFileName(p);
                    ins.Parameters["$d"].Value = folder;
                    ins.Parameters["$w"].Value = w;
                    ins.Parameters["$h"].Value = h;
                    ins.Parameters["$s"].Value = size;
                    ins.Parameters["$m"].Value = mtime;
                    ins.Parameters["$a"].Value = now;
                    ins.ExecuteNonQuery();
                    added++;
                }
            }
            tx.Commit();
        }

        // 移除已消失文件（限扫描过的文件夹）
        if (list.Count > 0)
        {
            var placeholders = string.Join(",", list.Select((_, i) => "$d" + i));
            using var del = conn.CreateCommand();
            del.CommandText = $"DELETE FROM images WHERE folder IN ({placeholders}) AND path NOT IN ({string.Join(",", all.Select((_, i) => "$p" + i))})";
            for (int i = 0; i < list.Count; i++) del.Parameters.AddWithValue("$d" + i, list[i]);
            for (int i = 0; i < all.Count; i++) del.Parameters.AddWithValue("$p" + i, all[i].Path);
            del.ExecuteNonQuery();
        }

        progress?.Report($"扫描完成：新增 {added}，更新 {updated}");
        return added;
    }

    /// <summary>从索引移除单条（文件被删除时）</summary>
    public void RemoveIndexEntry(string path)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM images WHERE path = $p";
        cmd.Parameters.AddWithValue("$p", path);
        cmd.ExecuteNonQuery();
    }

    /// <summary>从识别库（imgtag）移除该路径记录：先删人脸/标签关联，再删图片行</summary>
    public void RemoveImgTagEntry(string path)
    {
        if (!HasImgTagData) return;
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM it.image_faces WHERE image_id IN (SELECT id FROM it.images WHERE path = $p);
            DELETE FROM it.image_tags WHERE image_id IN (SELECT id FROM it.images WHERE path = $p);
            DELETE FROM it.images WHERE path = $p;
            """;
        cmd.Parameters.AddWithValue("$p", path);
        cmd.ExecuteNonQuery();
    }

    /// <summary>识别库中该路径的创建时间（created_at，无则空）</summary>
    public string GetImgTagCreatedAt(string path)
    {
        if (!HasImgTagData) return "";
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT created_at FROM it.images WHERE path = $p";
            cmd.Parameters.AddWithValue("$p", path);
            var v = cmd.ExecuteScalar();
            return v == null || v is DBNull ? "" : v.ToString() ?? "";
        }
        catch { return ""; }
    }

    /// <summary>更新索引文件名（重命名后）</summary>
    public void RenameIndexEntry(string oldPath, string newPath)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE images SET path=$n, filename=$f WHERE path=$o";
        cmd.Parameters.AddWithValue("$n", newPath);
        cmd.Parameters.AddWithValue("$f", Path.GetFileName(newPath));
        cmd.Parameters.AddWithValue("$o", oldPath);
        cmd.ExecuteNonQuery();
    }

    // ==================== 查询 ====================

    public int Count()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        if (HasIt(conn)) cmd.CommandText = "SELECT COUNT(DISTINCT path) FROM (SELECT path FROM images UNION SELECT path FROM it.images)";
        else cmd.CommandText = "SELECT COUNT(*) FROM images";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public List<ImgItem> Page(int offset, int limit)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        if (HasIt(conn))
        {
            cmd.CommandText = """
                SELECT path, filename, width, height, file_size,
                       MAX(description) AS description, MAX(tags) AS tags,
                       MAX(ord) AS ord, MAX(pri) AS pri, MAX(faces_scanned) AS faces_scanned
                FROM (
                    SELECT path, filename, width, height, file_size, '' AS description, '' AS tags, added_at AS ord, 0 AS pri, 0 AS faces_scanned FROM images
                    UNION
                    SELECT path, filename, width, height, file_size, description, tags,
                           CAST(strftime('%s', created_at) AS INTEGER) AS ord, 1 AS pri, faces_scanned FROM it.images
                )
                GROUP BY path
                ORDER BY ord DESC LIMIT $l OFFSET $o
                """;
        }
        else
        {
            cmd.CommandText = "SELECT path, filename, width, height, file_size, '' AS description, '' AS tags, added_at AS ord, 'folder' AS src, 0 AS faces_scanned FROM images ORDER BY ord DESC LIMIT $l OFFSET $o";
        }
        cmd.Parameters.AddWithValue("$l", limit);
        cmd.Parameters.AddWithValue("$o", offset);
        return ReadItems(cmd);
    }

    public int SearchCount(string q)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        var p = "%" + q + "%";
        if (HasIt(conn))
        {
            cmd.CommandText = """
                SELECT (SELECT COUNT(DISTINCT path) FROM (
                        SELECT path FROM images WHERE filename LIKE $p OR path LIKE $p
                        UNION SELECT path FROM it.images WHERE filename LIKE $p OR path LIKE $p OR description LIKE $p OR tags LIKE $p))
                """;
        }
        else
        {
            cmd.CommandText = "SELECT COUNT(*) FROM images WHERE filename LIKE $p OR path LIKE $p";
        }
        cmd.Parameters.AddWithValue("$p", p);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public List<ImgItem> Search(string q, int offset, int limit)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        var p = "%" + q + "%";
        if (HasIt(conn))
        {
            cmd.CommandText = """
                SELECT path, filename, width, height, file_size,
                       MAX(description) AS description, MAX(tags) AS tags,
                       MAX(ord) AS ord, MAX(pri) AS pri, MAX(faces_scanned) AS faces_scanned
                FROM (
                    SELECT path, filename, width, height, file_size, '' AS description, '' AS tags, added_at AS ord, 0 AS pri, 0 AS faces_scanned
                    FROM images WHERE filename LIKE $p OR path LIKE $p
                    UNION
                    SELECT path, filename, width, height, file_size, description, tags,
                           CAST(strftime('%s', created_at) AS INTEGER) AS ord, 1 AS pri, faces_scanned
                    FROM it.images WHERE filename LIKE $p OR path LIKE $p OR description LIKE $p OR tags LIKE $p
                )
                GROUP BY path
                ORDER BY ord DESC LIMIT $l OFFSET $o
                """;
        }
        else
        {
            cmd.CommandText = "SELECT path, filename, width, height, file_size, '' AS description, '' AS tags, added_at AS ord, 'folder' AS src, 0 AS faces_scanned FROM images WHERE filename LIKE $p OR path LIKE $p ORDER BY ord DESC LIMIT $l OFFSET $o";
        }
        cmd.Parameters.AddWithValue("$p", p);
        cmd.Parameters.AddWithValue("$l", limit);
        cmd.Parameters.AddWithValue("$o", offset);
        return ReadItems(cmd);
    }

    // ==================== 阶段3：统一筛选 / 排序 / 搜索（全部下推 SQL） ====================

    /// <summary>图片网格查询条件：关键词、排序、状态、来源夹、标签 AND 组合。</summary>
    public sealed class ImgQuery
    {
        public string Keyword { get; set; } = "";                       // 文件名/路径/描述/标签
        public string Sort { get; set; } = "date_desc";                 // date_desc/date_asc/name/size/pixels
        public string Status { get; set; } = "all";                     // all/no_face/has_face/tagged/untagged/has_prompt
        public string? Folder { get; set; }                             // 来源夹精确目录（null/空=全部）
        public List<long> TagIds { get; set; } = new();                 // 标签 AND 组合（同时含全部）
        public HashSet<string>? PromptFiles { get; set; }               // “有关联词”：命中的文件名集合
    }

    private bool? _ftsOk;

    private bool FtsAvailable(SqliteConnection conn)
    {
        if (_ftsOk.HasValue) return _ftsOk.Value;
        try
        {
            using var probe = conn.CreateCommand();
            probe.CommandText = "SELECT 1 FROM it.images_fts WHERE images_fts MATCH 'a' LIMIT 1";
            probe.ExecuteScalar();
            _ftsOk = true;
        }
        catch { _ftsOk = false; }
        return _ftsOk.Value;
    }

    /// <summary>FTS5 MATCH 串：拆词后用双引号包成 phrase（隐式 AND），剥离引号等危险字符；无有效词返回 null。</summary>
    private static string? ToFtsMatch(string? kw)
    {
        if (string.IsNullOrWhiteSpace(kw)) return null;
        var parts = kw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => new string(t.Where(ch => char.IsLetterOrDigit(ch) || ch > 127).ToArray()))
            .Where(t => t.Length > 0)
            .Select(t => "\"" + t.Replace("\"", "") + "*\"")
            .Take(8).ToList();
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    private static string OrderClause(string sort) => sort switch
    {
        "date_asc" => "ord ASC, path ASC",
        "name" => "filename COLLATE NOCASE ASC",
        "size" => "file_size DESC, path ASC",
        "pixels" => "(width*height) DESC, path ASC",
        _ => "ord DESC, path ASC"
    };

    /// <summary>路径是否位于任一来源根目录下（含根本身）。</summary>
    public static bool PathUnderAnyRoot(string path, IEnumerable<string> roots)
    {
        if (string.IsNullOrEmpty(path)) return false;
        foreach (var root in roots)
        {
            var r = (root ?? "").TrimEnd('\\', '/');
            if (r.Length == 0) continue;
            if (path.Equals(r, StringComparison.OrdinalIgnoreCase)) return true;
            if (path.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase)) return true;
            if (path.StartsWith(r + "/", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// 生成“路径位于 AllowedRoots 之下”的 SQL 条件（含参数绑定）。
    /// AllowedRoots 为 null 时不限制；空列表时恒假。
    /// </summary>
    private string RootFilterSql(List<(string Name, object? Value)> ps, string pathExpr)
    {
        var roots = AllowedRoots;
        if (roots == null) return "";
        if (roots.Count == 0) return " AND 1=0";
        var ors = new List<string>();
        for (int i = 0; i < roots.Count; i++)
        {
            var r = (roots[i] ?? "").TrimEnd('\\', '/');
            if (r.Length == 0) continue;
            var n = "$rt" + i;
            ps.Add((n, r));
            ors.Add($"(lower({pathExpr})=lower({n}) OR lower(substr({pathExpr},1,length({n})+1))=lower({n})||'\\')");
        }
        return ors.Count == 0 ? " AND 1=0" : " AND (" + string.Join(" OR ", ors) + ")";
    }

    /// <summary>构造中层“双库 UNION→按 path 分组聚合 + 状态/标签 HAVING”SQL 与命名参数。</summary>
    private (string Sql, List<(string Name, object? Value)> Params) BuildGrouped(SqliteConnection conn, ImgQuery q)
    {
        var ps = new List<(string, object?)>
        {
            ("$kw", "%" + (q.Keyword ?? "").Trim() + "%"),
            ("$folder", q.Folder ?? "")
        };
        var fts = ToFtsMatch(q.Keyword);
        var useFts = !string.IsNullOrEmpty(fts) && FtsAvailable(conn);
        if (useFts) ps.Add(("$fts", fts));

        // “有关联词”：文件名 IN 集合（两个子库都下推）
        string promptCond = "";
        if (q.Status == "has_prompt")
        {
            var pf = q.PromptFiles?.ToList() ?? new List<string>();
            if (pf.Count == 0) promptCond = " AND (1=0)"; // 无任何关联 → 空结果
            else
            {
                var names = new List<string>();
                for (int i = 0; i < pf.Count; i++)
                {
                    var n = "$pf" + i; names.Add(n); ps.Add((n, pf[i]));
                }
                promptCond = " AND filename IN (" + string.Join(",", names) + ")";
            }
        }

        var itKw = useFts
            ? "id IN (SELECT rowid FROM it.images_fts WHERE images_fts MATCH $fts)"
            : "(filename LIKE $kw OR path LIKE $kw OR description LIKE $kw OR tags LIKE $kw)";

        // 双侧都限制在已添加来源下；同一组 $rt 参数可被两处引用
        var rootSql = RootFilterSql(ps, "path");

        var inner = $"""
            SELECT path,filename,width,height,file_size,'' AS description,'' AS tags,added_at AS ord,0 AS pri,0 AS faces_scanned,CAST(NULL AS INTEGER) AS itid
            FROM images
            WHERE (filename LIKE $kw OR path LIKE $kw) AND ($folder='' OR folder=$folder){promptCond}{rootSql}
            UNION
            SELECT path,filename,width,height,file_size,description,tags,CAST(strftime('%s',created_at) AS INTEGER) AS ord,1 AS pri,faces_scanned,id AS itid
            FROM it.images
            WHERE ({itKw}) AND ($folder='' OR substr(path,1,length($folder))=$folder){promptCond}{rootSql}
            """;

        var having = "HAVING 1=1";
        switch (q.Status)
        {
            case "no_face": having += " AND gfs=0"; break;
            case "has_face": having += " AND gfs=1"; break;
            // 「已 AI 打标」与「未 AI 打标」互为补集：以描述是否非空判定（与 tagged 口径一致）
            case "tagged": having += " AND COALESCE(gdesc,'')<>''"; break;
            case "untagged": having += " AND COALESCE(gdesc,'')=''"; break;
        }
        if (q.TagIds.Count > 0)
        {
            var names = new List<string>();
            for (int i = 0; i < q.TagIds.Count; i++)
            {
                var n = "$tg" + i; names.Add(n); ps.Add((n, q.TagIds[i]));
            }
            having += $" AND (SELECT COUNT(DISTINCT t.tag_id) FROM it.image_tags t WHERE t.image_id=gitid AND t.tag_id IN ({string.Join(",", names)}))={q.TagIds.Count}";
        }

        var mid = $"""
            SELECT path, MAX(filename) filename, MAX(width) width, MAX(height) height, MAX(file_size) file_size,
                   MAX(description) gdesc, MAX(tags) tags, MAX(ord) ord, MAX(pri) pri, MAX(faces_scanned) gfs, MAX(itid) gitid
            FROM ({inner})
            GROUP BY path
            {having}
            """;
        return (mid, ps);
    }

    /// <summary>无 imgtag 库时的退化：只查自建索引。</summary>
    private (string Sql, List<(string, object?)> Params) BuildSelfOnly(ImgQuery q)
    {
        var ps = new List<(string, object?)> { ("$kw", "%" + (q.Keyword ?? "").Trim() + "%"), ("$folder", q.Folder ?? "") };
        string promptCond = "";
        if (q.Status == "has_prompt")
        {
            var pf = q.PromptFiles?.ToList() ?? new List<string>();
            if (pf.Count == 0) promptCond = " AND (1=0)";
            else
            {
                var names = new List<string>();
                for (int i = 0; i < pf.Count; i++) { var n = "$pf" + i; names.Add(n); ps.Add((n, pf[i])); }
                promptCond = " AND filename IN (" + string.Join(",", names) + ")";
            }
        }
        // 自建库无人脸/打标/标签：「已识别人脸 / 已 AI 打标 / 指定标签」这类状态必然为空；
        // 「未 AI 打标」在无识别库时全量命中，不做限制。
        var impossible = q.Status is "has_face" or "tagged" || q.TagIds.Count > 0;
        var rootSql = RootFilterSql(ps, "path");
        var mid = $"""
            SELECT path,filename,width,height,file_size,'' gdesc,'' tags,added_at ord,0 pri,0 gfs,CAST(NULL AS INTEGER) gitid
            FROM images
            WHERE (filename LIKE $kw OR path LIKE $kw) AND ($folder='' OR folder=$folder){promptCond}{rootSql}
            {(impossible ? "AND (1=0)" : "")}
            """;
        return (mid, ps);
    }

    private static void Bind(SqliteCommand cmd, List<(string Name, object? Value)> ps)
    {
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
    }

    public int QueryCount(ImgQuery q)
    {
        using var conn = Open();
        var (mid, ps) = HasIt(conn) ? BuildGrouped(conn, q) : BuildSelfOnly(q);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM ({mid})";
        Bind(cmd, ps);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public List<ImgItem> QueryPage(ImgQuery q, int offset, int limit)
    {
        using var conn = Open();
        var (mid, ps) = HasIt(conn) ? BuildGrouped(conn, q) : BuildSelfOnly(q);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT path,filename,width,height,file_size,gdesc,tags,ord,pri,gfs FROM ({mid})
            ORDER BY {OrderClause(q.Sort)}
            LIMIT $l OFFSET $o
            """;
        Bind(cmd, ps);
        cmd.Parameters.AddWithValue("$l", limit);
        cmd.Parameters.AddWithValue("$o", offset);
        return ReadItems(cmd);
    }

    /// <summary>当前 path 在“筛选+排序”完整结果中的 1-based 名次（找不到返回 0），供查看器跨页连续导航。</summary>
    public int QueryRank(ImgQuery q, string path)
    {
        using var conn = Open();
        var (mid, ps) = HasIt(conn) ? BuildGrouped(conn, q) : BuildSelfOnly(q);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT rn FROM (
                SELECT path, ROW_NUMBER() OVER (ORDER BY {OrderClause(q.Sort)}) AS rn FROM ({mid})
            ) WHERE path=$p
            """;
        Bind(cmd, ps);
        cmd.Parameters.AddWithValue("$p", path);
        var v = cmd.ExecuteScalar();
        return v == null ? 0 : Convert.ToInt32(v);
    }

    /// <summary>查看器跨页连续导航的序列来源：当前筛选/排序完整结果的只读随机访问。</summary>
    public interface IImgSequence
    {
        int Total { get; }                 // 结果总数
        int RankOf(string path);           // 当前图的 1-based 名次，找不到 0
        ImgItem? At(int zeroBasedRank);    // 取第 n 张（0-based），越界返回 null
    }

    /// <summary>仅自建索引库中已扫描过的来源夹（用于把历史来源回填到设置，避免丢失）。</summary>
    public List<string> DistinctSelfFolders()
    {
        var list = new List<string>();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT folder FROM images WHERE folder<>'' ORDER BY folder";
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(r.GetString(0));
        }
        catch { }
        return list;
    }

    /// <summary>来源夹列表：合并自建 folder 与 imgtag 路径目录，去重排序；仅保留已添加来源之下的目录。</summary>
    public List<string> DistinctFolders()
    {
        var list = new List<string>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        if (HasIt(conn))
        {
            cmd.CommandText = """
                SELECT DISTINCT f FROM (
                    SELECT folder AS f FROM images WHERE folder<>''
                    UNION
                    SELECT substr(path,1,length(path)-length(filename)-1) AS f FROM it.images
                ) WHERE f<>'' ORDER BY f
                """;
        }
        else cmd.CommandText = "SELECT DISTINCT folder AS f FROM images WHERE folder<>'' ORDER BY f";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        if (AllowedRoots != null)
            list = list.Where(f => PathUnderAnyRoot(f, AllowedRoots)).ToList();
        return list;
    }

    // ==================== 识别数据（imgtag 只读） ====================

    public bool HasImgTagData => !string.IsNullOrWhiteSpace(_imgtagDbPath) && File.Exists(_imgtagDbPath);

    /// <summary>某文件夹下已入库的图片数（自建索引与识别库取较大值，两库通常同步）</summary>
    public int CountImagesInFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return 0;
        int a = 0, b = 0;
        try
        {
            using var conn = Open();
            using (var c = conn.CreateCommand())
            {
                c.CommandText = "SELECT COUNT(*) FROM images WHERE folder=$f";
                c.Parameters.AddWithValue("$f", folder);
                a = Convert.ToInt32(c.ExecuteScalar());
            }
            if (HasIt(conn))
            {
                using var c2 = conn.CreateCommand();
                c2.CommandText = "SELECT COUNT(*) FROM it.images WHERE substr(path,1,length($f)+1)=$f || '\\'";
                c2.Parameters.AddWithValue("$f", folder);
                b = Convert.ToInt32(c2.ExecuteScalar());
            }
        }
        catch { }
        return Math.Max(a, b);
    }

    /// <summary>
    /// 清理「路径不在 AllowedRoots 之下」的索引记录（**只删索引/识别库记录，不动磁盘图片**）。
    /// AllowedRoots 为 null 或空列表时不做任何事（避免误清全库）。返回删除的图片记录数。
    /// </summary>
    public int RemoveIndexOutsideRoots()
    {
        var roots = AllowedRoots;
        if (roots == null || roots.Count == 0) return 0;
        try
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            int n = 0;
            using (var c = conn.CreateCommand())
            {
                c.CommandText = "SELECT path FROM main.images";
                var outside = new List<string>();
                using (var r = c.ExecuteReader())
                {
                    while (r.Read())
                    {
                        var p = r.GetString(0);
                        if (!PathUnderAnyRoot(p, roots)) outside.Add(p);
                    }
                }
                foreach (var p in outside)
                {
                    // 自建库可能没有 image_tags 表；表名加 main. 前缀，避免解析到 it.image_tags
                    using (var d = conn.CreateCommand())
                    {
                        d.Transaction = tx;
                        d.CommandText = "DELETE FROM main.image_tags WHERE image_id IN (SELECT id FROM main.images WHERE path=$p)";
                        d.Parameters.AddWithValue("$p", p);
                        try { n += d.ExecuteNonQuery(); } catch { /* 表不存在则忽略 */ }
                    }
                    using (var d = conn.CreateCommand())
                    {
                        d.Transaction = tx;
                        d.CommandText = "DELETE FROM main.images WHERE path=$p";
                        d.Parameters.AddWithValue("$p", p);
                        n += d.ExecuteNonQuery();
                    }
                }
            }
            if (HasIt(conn))
            {
                using var c = conn.CreateCommand();
                c.CommandText = "SELECT path FROM it.images";
                var outside = new List<string>();
                using (var r = c.ExecuteReader())
                {
                    while (r.Read())
                    {
                        var p = r.GetString(0);
                        if (!PathUnderAnyRoot(p, roots)) outside.Add(p);
                    }
                }
                foreach (var p in outside)
                {
                    foreach (var sql in new[]
                    {
                        "DELETE FROM it.image_faces WHERE image_id IN (SELECT id FROM it.images WHERE path=$p)",
                        "DELETE FROM it.image_tags WHERE image_id IN (SELECT id FROM it.images WHERE path=$p)",
                        "DELETE FROM it.images WHERE path=$p",
                    })
                    {
                        using var d = conn.CreateCommand();
                        d.Transaction = tx;
                        d.CommandText = sql;
                        d.Parameters.AddWithValue("$p", p);
                        n += d.ExecuteNonQuery();
                    }
                }
            }
            tx.Commit();
            return n;
        }
        catch { return 0; }
    }

    /// <summary>把某文件夹的图片索引从两库中移除（**只删索引记录，不动磁盘文件**）。
    /// 用于「移除文件夹来源」时同步清理，使来源筛选不再残留该目录。
    /// </summary>
    public int RemoveFolderIndex(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return 0;
        try
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            int n = 0;

            // 自建索引库：先清关联的标签/人脸（外键未必启用，手动清更稳），再删主记录
            TryExec(conn, tx, "DELETE FROM image_tags WHERE image_id IN (SELECT id FROM images WHERE folder=$f)", folder);
            using (var c = conn.CreateCommand())
            {
                c.Transaction = tx;
                c.CommandText = "DELETE FROM images WHERE folder=$f";
                c.Parameters.AddWithValue("$f", folder);
                n += c.ExecuteNonQuery();
            }

            // 识别库（it）：人脸依赖图片，先删人脸再删图片
            if (HasIt(conn))
            {
                TryExec(conn, tx, "DELETE FROM it.image_faces WHERE image_id IN (SELECT id FROM it.images WHERE substr(path,1,length($f)+1)=$f || '\\')", folder);
                TryExec(conn, tx, "DELETE FROM it.image_tags WHERE image_id IN (SELECT id FROM it.images WHERE substr(path,1,length($f)+1)=$f || '\\')", folder);
                using var c2 = conn.CreateCommand();
                c2.Transaction = tx;
                c2.CommandText = "DELETE FROM it.images WHERE substr(path,1,length($f)+1)=$f || '\\'";
                c2.Parameters.AddWithValue("$f", folder);
                n += c2.ExecuteNonQuery();
            }

            tx.Commit();
            if (n > 0) { try { PruneEmptyPersons(); } catch { } }
            return n;
        }
        catch { return 0; }
    }

    /// <summary>
    /// **判定「文件真的没了」的唯一入口**：文件不存在 **且** 所在盘可访问。
    /// 不加盘符判断的话，移动盘 / 网络盘一掉线，整页照片都会被当成"已删除"清掉
    /// （0.62.3 修的就是这个：访问证件照页回来后图片库空了）。
    /// 所有"顺带清理索引"的地方都必须调它，不许自己写 `!File.Exists(x)`。
    /// </summary>
    public static bool IsMissing(string path)
    {
        try { return !File.Exists(path) && DriveAvailable(path); }
        catch { return false; }
    }

    /// <summary>文件所在磁盘是否可用（移动盘未连接时返回 false，用于避免误判为"失效"）</summary>
    private static bool DriveAvailable(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            return !string.IsNullOrEmpty(root) && Directory.Exists(root);
        }
        catch { return false; }
    }

    /// <summary>
    /// 统计「索引里有记录、但磁盘文件已不存在」的图片数（自建索引 + 识别库）。
    /// 用于清理被删除文件夹留下的残留记录（这些记录会让「来源」筛选残留已删除的目录）。
    /// 所在磁盘不可用时不计入，避免移动硬盘未连接时误判。
    /// </summary>
    public int CountMissingImages()
    {
        int n = 0;
        try
        {
            using var conn = Open();
            using (var c = conn.CreateCommand())
            {
                c.CommandText = "SELECT path FROM images";
                using var r = c.ExecuteReader();
                while (r.Read()) { var p = r.GetString(0); if (!File.Exists(p) && DriveAvailable(p)) n++; }
            }
            if (HasIt(conn))
            {
                using var c2 = conn.CreateCommand();
                c2.CommandText = "SELECT path FROM it.images";
                using var r2 = c2.ExecuteReader();
                while (r2.Read()) { var p = r2.GetString(0); if (!File.Exists(p) && DriveAvailable(p)) n++; }
            }
        }
        catch { }
        return n;
    }

    /// <summary>
    /// 清理磁盘文件已不存在的图片索引（两库都清，**不动磁盘**）。返回清理的条目数。
    /// 注意：磁盘/移动盘未连接时不要调用，否则会把暂时不可用的照片一并清掉。
    /// </summary>
    public int RemoveMissingIndex()
    {
        try
        {
            using var conn = Open();

            var idxIds = new List<long>();
            using (var c = conn.CreateCommand())
            {
                c.CommandText = "SELECT id, path FROM images";
                using var r = c.ExecuteReader();
                while (r.Read())
                {
                    var p = r.GetString(1);
                    if (!File.Exists(p) && DriveAvailable(p)) idxIds.Add(r.GetInt64(0));
                }
            }

            var itIds = new List<long>();
            if (HasIt(conn))
            {
                using var c2 = conn.CreateCommand();
                c2.CommandText = "SELECT id, path FROM it.images";
                using var r2 = c2.ExecuteReader();
                while (r2.Read())
                {
                    var p = r2.GetString(1);
                    if (!File.Exists(p) && DriveAvailable(p)) itIds.Add(r2.GetInt64(0));
                }
            }

            if (idxIds.Count == 0 && itIds.Count == 0) return 0;
            int n = 0;
            using var tx = conn.BeginTransaction();

            for (int off = 0; off < idxIds.Count; off += 400)
            {
                var chunk = idxIds.Skip(off).Take(400).ToList();
                var ph = string.Join(",", chunk.Select((_, i) => "$i" + i));
                TryExecIds(conn, tx, $"DELETE FROM image_tags WHERE image_id IN ({ph})", chunk);
                n += TryExecIds(conn, tx, $"DELETE FROM images WHERE id IN ({ph})", chunk);
            }
            for (int off = 0; off < itIds.Count; off += 400)
            {
                var chunk = itIds.Skip(off).Take(400).ToList();
                var ph = string.Join(",", chunk.Select((_, i) => "$i" + i));
                TryExecIds(conn, tx, $"DELETE FROM it.image_faces WHERE image_id IN ({ph})", chunk, true);
                TryExecIds(conn, tx, $"DELETE FROM it.image_tags WHERE image_id IN ({ph})", chunk, true);
                n += TryExecIds(conn, tx, $"DELETE FROM it.images WHERE id IN ({ph})", chunk, true);
            }

            tx.Commit();
            if (n > 0) { try { PruneEmptyPersons(); } catch { } }
            return n;
        }
        catch { return 0; }
    }

    private static int TryExecIds(SqliteConnection conn, SqliteTransaction tx, string sql, List<long> ids, bool silent = false)
    {
        try
        {
            using var c = conn.CreateCommand();
            c.Transaction = tx;
            c.CommandText = sql;
            for (int i = 0; i < ids.Count; i++) c.Parameters.AddWithValue("$i" + i, ids[i]);
            return c.ExecuteNonQuery();
        }
        catch { return 0; }
    }

    /// <summary>执行可能因表不存在而失败的清理语句（不影响主流程）</summary>
    private static void TryExec(SqliteConnection conn, SqliteTransaction tx, string sql, string folder)
    {
        try
        {
            using var c = conn.CreateCommand();
            c.Transaction = tx;
            c.CommandText = sql;
            c.Parameters.AddWithValue("$f", folder);
            c.ExecuteNonQuery();
        }
        catch { }
    }

    /// <summary>人物列表（过滤 ignored 与 0 张照片的孤儿人物，按照片数降序）</summary>
    public List<PersonItem> Persons()
    {
        using var conn = Open();
        if (!HasIt(conn)) return new List<PersonItem>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT p.id, p.name, p.avatar_face_id,
                   (SELECT COUNT(DISTINCT f.image_id) FROM it.image_faces f WHERE f.person_id = p.id) AS c,
                   (SELECT i.path FROM it.image_faces f2 JOIN it.images i ON i.id = f2.image_id
                     WHERE f2.person_id = p.id ORDER BY f2.id LIMIT 1) AS sample
            FROM it.persons p
            WHERE p.ignored = 0
              AND EXISTS (
                  SELECT 1 FROM it.image_faces f
                  WHERE f.person_id = p.id
                    AND EXISTS (SELECT 1 FROM it.images i WHERE i.id = f.image_id)
              )
            ORDER BY c DESC
            """;
        var list = new List<PersonItem>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new PersonItem
            {
                Id = r.GetInt64(0),
                Name = r.GetString(1),
                AvatarFaceId = r.IsDBNull(2) ? null : r.GetInt64(2),
                PhotoCount = r.GetInt32(3),
                SamplePath = r.IsDBNull(4) ? "" : r.GetString(4)
            });
        }
        return list;
    }

    /// <summary>人物的全部人脸（换封面候选，按分数降序）</summary>
    public List<PersonFaceItem> PersonFaces(long personId, int limit = 500)
    {
        var list = new List<PersonFaceItem>();
        if (!HasImgTagData) return list;
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, image_id, bbox, score FROM it.image_faces WHERE person_id=$p ORDER BY score DESC LIMIT $l";
            cmd.Parameters.AddWithValue("$p", personId);
            cmd.Parameters.AddWithValue("$l", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new PersonFaceItem
                {
                    FaceId = r.GetInt64(0),
                    ImageId = r.GetInt64(1),
                    Bbox = r.GetString(2),
                    Score = r.IsDBNull(3) ? 0 : r.GetDouble(3)
                });
            }
        }
        catch { }
        return list;
    }

    /// <summary>重命名人物</summary>
    public bool RenamePerson(long personId, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE it.persons SET name=$n WHERE id=$p";
            cmd.Parameters.AddWithValue("$n", name.Trim());
            cmd.Parameters.AddWithValue("$p", personId);
            return cmd.ExecuteNonQuery() > 0;
        }
        catch { return false; }
    }

    /// <summary>设置人物封面（faceId 必须属于该人物）</summary>
    public bool SetPersonAvatar(long personId, long faceId)
    {
        try
        {
            using var conn = Open();
            using var chk = conn.CreateCommand();
            chk.CommandText = "SELECT COUNT(*) FROM it.image_faces WHERE id=$f AND person_id=$p";
            chk.Parameters.AddWithValue("$f", faceId);
            chk.Parameters.AddWithValue("$p", personId);
            if (Convert.ToInt32(chk.ExecuteScalar()) == 0) return false;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE it.persons SET avatar_face_id=$f WHERE id=$p";
            cmd.Parameters.AddWithValue("$f", faceId);
            cmd.Parameters.AddWithValue("$p", personId);
            return cmd.ExecuteNonQuery() > 0;
        }
        catch { return false; }
    }

    /// <summary>忽略 / 取消忽略人物。
    /// 忽略后：不出现在人物列表、不在查看器画人脸框、不参与后续识别匹配（等同永久忽略）。</summary>
    public bool SetPersonIgnored(long personId, bool ignored)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE it.persons SET ignored=$v WHERE id=$p";
            cmd.Parameters.AddWithValue("$v", ignored ? 1 : 0);
            cmd.Parameters.AddWithValue("$p", personId);
            return cmd.ExecuteNonQuery() > 0;
        }
        catch { return false; }
    }

    /// <summary>已忽略的人物列表（供设置页查看与恢复）</summary>
    public List<PersonItem> IgnoredPersons()
    {
        var list = new List<PersonItem>();
        if (!HasImgTagData) return list;
        try
        {
            using var conn = Open();
            if (!HasIt(conn)) return list;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT p.id, p.name, p.avatar_face_id,
                       (SELECT COUNT(DISTINCT f.image_id) FROM it.image_faces f WHERE f.person_id = p.id) AS c,
                       (SELECT i.path FROM it.image_faces f2 JOIN it.images i ON i.id = f2.image_id
                         WHERE f2.person_id = p.id ORDER BY f2.id LIMIT 1) AS sample
                FROM it.persons p
                WHERE p.ignored = 1
                ORDER BY c DESC
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new PersonItem
                {
                    Id = r.GetInt64(0),
                    Name = r.IsDBNull(1) ? "" : r.GetString(1),
                    AvatarFaceId = r.IsDBNull(2) ? null : r.GetInt64(2),
                    PhotoCount = r.GetInt32(3),
                    SamplePath = r.IsDBNull(4) ? "" : r.GetString(4)
                });
            }
        }
        catch { }
        return list;
    }

    /// <summary>
    /// 把指定图片中属于该人物的人脸移出该人物（删除这些人脸记录）。
    /// 只影响「这些图片 × 该人物」的人脸，**不删除图片本身**，也不影响其他人物。
    /// 返回实际移除的人脸条数。
    /// </summary>
    public int RemoveFacesFromPerson(long personId, IEnumerable<string> imagePaths)
    {
        var paths = imagePaths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (personId <= 0 || paths.Count == 0) return 0;
        try
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            int total = 0;
            // 分批，避开 SQLite 参数上限
            for (int off = 0; off < paths.Count; off += 400)
            {
                var chunk = paths.Skip(off).Take(400).ToList();
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                var ph = string.Join(",", chunk.Select((_, i) => "$p" + i));
                cmd.CommandText =
                    $"DELETE FROM it.image_faces WHERE person_id=$pid AND image_id IN " +
                    $"(SELECT id FROM it.images WHERE path IN ({ph}))";
                cmd.Parameters.AddWithValue("$pid", personId);
                for (int i = 0; i < chunk.Count; i++) cmd.Parameters.AddWithValue("$p" + i, chunk[i]);
                total += cmd.ExecuteNonQuery();
            }
            tx.Commit();
            // 若该人物已被清空，顺手清掉空人物（保留 ignored 的主动忽略项）
            try { PruneEmptyPersons(); } catch { }
            return total;
        }
        catch { return 0; }
    }

    /// <summary>
    /// 按人脸特征相似度推荐「可能其实是同一个人」的人物对，供一键合并。
    /// 聚类阈值是 0.6（相似度 ≥ 0.6 会被直接聚成同一人），因此候选通常落在 [threshold, 0.6)。
    /// 取每个人物全部人脸的平均特征再两两比较，按相似度降序返回。
    /// </summary>
    public List<MergeSuggestion> SuggestMerges(float threshold = 0.55f, int maxResults = 60)
    {
        var result = new List<MergeSuggestion>();
        if (!HasImgTagData) return result;
        try
        {
            var acc = new Dictionary<long, (double[] Sum, int N, string Name, long? Avatar)>();
            var photoCount = new Dictionary<long, int>();

            using var conn = Open();
            if (!HasIt(conn)) return result;

            using (var pc = conn.CreateCommand())
            {
                pc.CommandText = "SELECT person_id, COUNT(DISTINCT image_id) FROM it.image_faces WHERE person_id IS NOT NULL GROUP BY person_id";
                using var pr = pc.ExecuteReader();
                while (pr.Read()) if (!pr.IsDBNull(0)) photoCount[pr.GetInt64(0)] = pr.GetInt32(1);
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT f.person_id, f.embedding, ifnull(p.name,''), p.avatar_face_id
                    FROM it.image_faces f
                    JOIN it.persons p ON p.id = f.person_id
                    WHERE p.ignored = 0 AND f.embedding IS NOT NULL
                    """;
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var pid = r.GetInt64(0);
                    if (r.IsDBNull(1)) continue;
                    var bytes = (byte[])r.GetValue(1);
                    if (bytes.Length < 128 || bytes.Length % 4 != 0) continue;
                    int dim = bytes.Length / 4;
                    if (!acc.TryGetValue(pid, out var e) || e.Sum.Length != dim)
                        e = (new double[dim], 0, r.IsDBNull(2) ? "" : r.GetString(2), r.IsDBNull(3) ? null : r.GetInt64(3));
                    var fv = new float[dim];
                    Buffer.BlockCopy(bytes, 0, fv, 0, bytes.Length);
                    for (int i = 0; i < dim; i++) e.Sum[i] += fv[i];
                    e.N++;
                    acc[pid] = e;
                }
            }

            // 平均 + 归一化
            var ids = new List<long>();
            var vecs = new List<float[]>();
            var names = new List<string>();
            var avatars = new List<long?>();
            foreach (var kv in acc)
            {
                if (kv.Value.N == 0) continue;
                var v = new float[kv.Value.Sum.Length];
                double norm = 0;
                for (int i = 0; i < v.Length; i++) { v[i] = (float)(kv.Value.Sum[i] / kv.Value.N); norm += (double)v[i] * v[i]; }
                norm = Math.Sqrt(norm);
                if (norm <= 0) continue;
                for (int i = 0; i < v.Length; i++) v[i] = (float)(v[i] / norm);
                ids.Add(kv.Key); vecs.Add(v); names.Add(kv.Value.Name); avatars.Add(kv.Value.Avatar);
            }

            // 两两比较
            for (int i = 0; i < ids.Count; i++)
            {
                for (int j = i + 1; j < ids.Count; j++)
                {
                    int len = Math.Min(vecs[i].Length, vecs[j].Length);
                    float sim = 0;
                    for (int k = 0; k < len; k++) sim += vecs[i][k] * vecs[j][k];
                    if (sim < threshold) continue;
                    result.Add(new MergeSuggestion
                    {
                        PersonIdA = ids[i],
                        PersonIdB = ids[j],
                        NameA = names[i],
                        NameB = names[j],
                        CountA = photoCount.TryGetValue(ids[i], out var c1) ? c1 : 0,
                        CountB = photoCount.TryGetValue(ids[j], out var c2) ? c2 : 0,
                        AvatarFaceIdA = avatars[i],
                        AvatarFaceIdB = avatars[j],
                        Similarity = sim
                    });
                }
            }
            result.Sort((a, b) => b.Similarity.CompareTo(a.Similarity));
            // 同一个人物常常与多个条目都相似，若全量返回会产生海量重叠候选。
            // 这里按相似度从高到低贪心取「互不重叠」的组：每个条目最多出现在一条建议里。
            // 执行合并后可再次点击「建议合并」，让剩余相似项继续收敛。
            var used = new HashSet<long>();
            var filtered = new List<MergeSuggestion>();
            foreach (var s in result)
            {
                if (used.Contains(s.PersonIdA) || used.Contains(s.PersonIdB)) continue;
                used.Add(s.PersonIdA);
                used.Add(s.PersonIdB);
                filtered.Add(s);
                if (filtered.Count >= maxResults) break;
            }
            result = filtered;
        }
        catch { }
        return result;
    }

    /// <summary>合并人物：把 sourceIds 的人脸全部迁移到 targetId，删除 source</summary>
    public bool MergePersons(List<long> sourceIds, long targetId)
    {
        try
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            foreach (var sid in sourceIds)
            {
                if (sid == targetId) continue;
                // 迁移人脸
                using (var mv = conn.CreateCommand())
                {
                    mv.Transaction = tx;
                    mv.CommandText = "UPDATE it.image_faces SET person_id=$t WHERE person_id=$s";
                    mv.Parameters.AddWithValue("$t", targetId);
                    mv.Parameters.AddWithValue("$s", sid);
                    mv.ExecuteNonQuery();
                }
                // 目标无封面时继承来源封面
                using (var ta = conn.CreateCommand())
                {
                    ta.Transaction = tx;
                    ta.CommandText = "SELECT avatar_face_id FROM it.persons WHERE id=$t";
                    ta.Parameters.AddWithValue("$t", targetId);
                    var av = ta.ExecuteScalar();
                    if (av == null || av is DBNull)
                    {
                        using var sa = conn.CreateCommand();
                        sa.Transaction = tx;
                        sa.CommandText = "SELECT avatar_face_id FROM it.persons WHERE id=$s";
                        sa.Parameters.AddWithValue("$s", sid);
                        var sav = sa.ExecuteScalar();
                        if (sav != null && sav is not DBNull)
                        {
                            using var up = conn.CreateCommand();
                            up.Transaction = tx;
                            up.CommandText = "UPDATE it.persons SET avatar_face_id=$a WHERE id=$t";
                            up.Parameters.AddWithValue("$a", Convert.ToInt64(sav));
                            up.Parameters.AddWithValue("$t", targetId);
                            up.ExecuteNonQuery();
                        }
                    }
                }
                using (var del = conn.CreateCommand())
                {
                    del.Transaction = tx;
                    del.CommandText = "DELETE FROM it.persons WHERE id=$s";
                    del.Parameters.AddWithValue("$s", sid);
                    del.ExecuteNonQuery();
                }
            }
            tx.Commit();
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// 删除人物。
    /// deleteFaces=true：删掉其人脸框，并把相关图片 faces_scanned=0，下次识别人脸会重新检测。
    /// deleteFaces=false：保留人脸框，先解除归属再按特征自动归入其它人物（可再次被人物体系识别到）。
    /// </summary>
    public bool DeletePerson(long personId, bool deleteFaces)
    {
        try
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            if (deleteFaces)
            {
                List<long> imgIds = new();
                using (var sel = conn.CreateCommand())
                {
                    sel.Transaction = tx;
                    sel.CommandText = "SELECT DISTINCT image_id FROM it.image_faces WHERE person_id=$p";
                    sel.Parameters.AddWithValue("$p", personId);
                    using var r = sel.ExecuteReader();
                    while (r.Read()) imgIds.Add(r.GetInt64(0));
                }
                using (var df = conn.CreateCommand())
                {
                    df.Transaction = tx;
                    df.CommandText = "DELETE FROM it.image_faces WHERE person_id=$p";
                    df.Parameters.AddWithValue("$p", personId);
                    df.ExecuteNonQuery();
                }
                foreach (var iid in imgIds)
                {
                    using var rs = conn.CreateCommand();
                    rs.Transaction = tx;
                    rs.CommandText = "UPDATE it.images SET faces_scanned=0 WHERE id=$i";
                    rs.Parameters.AddWithValue("$i", iid);
                    rs.ExecuteNonQuery();
                }
            }
            else
            {
                // 保留人脸框：解除与本人物的归属，随后自动归类到其它人物，避免悬空
                using (var un = conn.CreateCommand())
                {
                    un.Transaction = tx;
                    un.CommandText = "UPDATE it.image_faces SET person_id=NULL WHERE person_id=$p";
                    un.Parameters.AddWithValue("$p", personId);
                    un.ExecuteNonQuery();
                }
            }
            using var del = conn.CreateCommand();
            del.Transaction = tx;
            del.CommandText = "DELETE FROM it.persons WHERE id=$p";
            del.Parameters.AddWithValue("$p", personId);
            del.ExecuteNonQuery();
            tx.Commit();
            if (!deleteFaces) ReassignOrphanFaces();
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// 把 person_id 为空的人脸按余弦相似度归入已有人物（阈值 0.55，与 standard 聚类一致）。
    /// 用于「删人物但保留人脸框」后重新归类，让人物页继续完整。
    /// </summary>
    public int ReassignOrphanFaces()
    {
        const float thresh = 0.55f;
        try
        {
            using var conn = Open();
            // 人物代表特征（各取最多 8 张人脸均值）
            var persons = new List<(long Id, float[] Emb)>();
            using (var pc = conn.CreateCommand())
            {
                pc.CommandText = "SELECT id FROM it.persons WHERE ignored=0";
                using var pr = pc.ExecuteReader();
                while (pr.Read()) persons.Add((pr.GetInt64(0), Array.Empty<float>()));
            }
            var pmap = new Dictionary<long, float[]>();
            foreach (var (pid, _) in persons)
            {
                var vecs = new List<float[]>();
                using var fc = conn.CreateCommand();
                fc.CommandText = "SELECT embedding FROM it.image_faces WHERE person_id=$p AND embedding IS NOT NULL LIMIT 8";
                fc.Parameters.AddWithValue("$p", pid);
                using var fr = fc.ExecuteReader();
                while (fr.Read())
                {
                    if (fr.IsDBNull(0)) continue;
                    var emb = BlobToL2((byte[])fr.GetValue(0));
                    if (emb != null) vecs.Add(emb);
                }
                if (vecs.Count == 0) continue;
                var dim = vecs[0].Length;
                var mean = new float[dim];
                foreach (var v in vecs)
                    for (int i = 0; i < dim; i++) mean[i] += v[i];
                float n = 0;
                foreach (var x in mean) n += x * x;
                n = MathF.Sqrt(n);
                if (n > 0) for (int i = 0; i < dim; i++) mean[i] /= n;
                pmap[pid] = mean;
            }

            var orphans = new List<(long FaceId, float[] Emb)>();
            using (var oc = conn.CreateCommand())
            {
                oc.CommandText = "SELECT id, embedding FROM it.image_faces WHERE person_id IS NULL AND embedding IS NOT NULL";
                using var or_ = oc.ExecuteReader();
                while (or_.Read())
                {
                    if (or_.IsDBNull(1)) continue;
                    var emb = BlobToL2((byte[])or_.GetValue(1));
                    if (emb != null) orphans.Add((or_.GetInt64(0), emb));
                }
            }
            if (orphans.Count == 0) return 0;

            int assigned = 0;
            using var tx = conn.BeginTransaction();
            foreach (var (fid, emb) in orphans)
            {
                long best = -1;
                float bestSim = thresh;
                foreach (var (pid, refEmb) in pmap)
                {
                    float s = Cos(emb, refEmb);
                    if (s > bestSim) { bestSim = s; best = pid; }
                }
                if (best < 0) continue;
                using var up = conn.CreateCommand();
                up.Transaction = tx;
                up.CommandText = "UPDATE it.image_faces SET person_id=$p WHERE id=$f";
                up.Parameters.AddWithValue("$p", best);
                up.Parameters.AddWithValue("$f", fid);
                up.ExecuteNonQuery();
                assigned++;
            }
            tx.Commit();
            return assigned;
        }
        catch { return 0; }
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

    private static float Cos(float[] a, float[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        float s = 0;
        for (int i = 0; i < n; i++) s += a[i] * b[i];
        return s;
    }

    /// <summary>统计空人物数量：ignored=0 且不存在"仍指向有效图片的人脸"（与 Persons() 显示过滤同口径）</summary>
    public int CountEmptyPersons()
    {
        if (!HasImgTagData) return 0;
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT COUNT(*) FROM it.persons p WHERE p.ignored=0 AND NOT EXISTS (
                    SELECT 1 FROM it.image_faces f JOIN it.images i ON i.id=f.image_id
                    WHERE f.person_id=p.id)
                """;
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
        catch { return 0; }
    }

    /// <summary>清理空人物（保留 ignored=1 的主动忽略项）。返回删除条数。</summary>
    public int PruneEmptyPersons()
    {
        if (!HasImgTagData) return 0;
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DELETE FROM it.persons WHERE ignored=0 AND NOT EXISTS (
                    SELECT 1 FROM it.image_faces f JOIN it.images i ON i.id=f.image_id
                    WHERE f.person_id=it.persons.id)
                """;
            return cmd.ExecuteNonQuery();
        }
        catch { return 0; }
    }

    /// <summary>人物照片（按图片倒序；仅已添加来源下）</summary>
    public List<ImgItem> PersonPhotos(long personId, int offset, int limit)
    {
        using var conn = Open();
        if (!HasIt(conn)) return new List<ImgItem>();
        var ps = new List<(string, object?)>();
        var rootSql = RootFilterSql(ps, "i.path");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT i.path, i.filename, i.width, i.height, i.file_size, i.description, i.tags, i.id AS ord, 'imgtag' AS src, i.faces_scanned
            FROM it.image_faces f JOIN it.images i ON i.id = f.image_id
            WHERE f.person_id = $p{rootSql}
            GROUP BY i.id
            ORDER BY i.id DESC LIMIT $l OFFSET $o
            """;
        Bind(cmd, ps);
        cmd.Parameters.AddWithValue("$p", personId);
        cmd.Parameters.AddWithValue("$l", limit);
        cmd.Parameters.AddWithValue("$o", offset);
        return ReadItems(cmd);
    }

    public int PersonPhotoCount(long personId)
    {
        using var conn = Open();
        if (!HasIt(conn)) return 0;
        var ps = new List<(string, object?)>();
        var rootSql = RootFilterSql(ps, "i.path");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(DISTINCT f.image_id) FROM it.image_faces f JOIN it.images i ON i.id=f.image_id WHERE f.person_id = $p{rootSql}";
        Bind(cmd, ps);
        cmd.Parameters.AddWithValue("$p", personId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>
    /// 某张图在人物相册里的 1-based 名次（0 = 不在结果中）。
    /// 排序必须与 <see cref="PersonPhotos"/> 一致（i.id DESC），供查看器左右导航定位。
    /// </summary>
    public int PersonPhotoRank(long personId, string path)
    {
        if (personId <= 0 || string.IsNullOrEmpty(path)) return 0;
        using var conn = Open();
        if (!HasIt(conn)) return 0;
        var ps = new List<(string, object?)>();
        var rootSql = RootFilterSql(ps, "i.path");

        // ① 先确认这张图本身在结果集内；不在就直接返回 0
        using (var c0 = conn.CreateCommand())
        {
            c0.CommandText = $"SELECT COUNT(*) FROM it.image_faces f JOIN it.images i ON i.id=f.image_id WHERE f.person_id=$p AND i.path=$path{rootSql}";
            Bind(c0, ps);
            c0.Parameters.AddWithValue("$p", personId);
            c0.Parameters.AddWithValue("$path", path);
            if (Convert.ToInt32(c0.ExecuteScalar() ?? 0) == 0) return 0;
        }

        // ② 排序与 PersonPhotos 一致（i.id DESC）→ 名次 = 排在它前面的条数 + 1
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT COUNT(DISTINCT i.id) FROM it.image_faces f JOIN it.images i ON i.id=f.image_id
            WHERE f.person_id=$p{rootSql}
              AND i.id > (SELECT id FROM it.images WHERE path=$path LIMIT 1)
            """;
        Bind(cmd, ps);
        cmd.Parameters.AddWithValue("$p", personId);
        cmd.Parameters.AddWithValue("$path", path);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0) + 1;
    }

    /// <summary>标签总数</summary>
    public int TagTotal()
    {
        using var conn = Open();
        if (!HasIt(conn)) return 0;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM it.tags";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>标签列表（按使用次数降序）。keyword 非空时按名称过滤；默认只取前 limit 个避免一次性渲染数千芯片。</summary>
    public List<TagItem> Tags(string? keyword = null, int limit = 200)
    {
        using var conn = Open();
        if (!HasIt(conn)) return new List<TagItem>();
        using var cmd = conn.CreateCommand();
        var p = "%" + (keyword ?? "") + "%";
        cmd.CommandText = """
            SELECT t.id, t.name, COUNT(it.image_id) AS c
            FROM it.tags t JOIN it.image_tags it ON it.tag_id = t.id
            GROUP BY t.id
            HAVING ($p = '%%' OR t.name LIKE $p)
            ORDER BY c DESC
            LIMIT $l
            """;
        cmd.Parameters.AddWithValue("$p", p);
        cmd.Parameters.AddWithValue("$l", limit);
        var list = new List<TagItem>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new TagItem { Id = r.GetInt64(0), Name = r.GetString(1), Count = r.GetInt32(2) });
        }
        return list;
    }

    /// <summary>标签照片（仅已添加来源下）</summary>
    public List<ImgItem> TagPhotos(long tagId, int offset, int limit)
    {
        using var conn = Open();
        if (!HasIt(conn)) return new List<ImgItem>();
        var ps = new List<(string, object?)>();
        var rootSql = RootFilterSql(ps, "i.path");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT i.path, i.filename, i.width, i.height, i.file_size, i.description, i.tags, i.id AS ord, 'imgtag' AS src, i.faces_scanned
            FROM it.image_tags it JOIN it.images i ON i.id = it.image_id
            WHERE it.tag_id = $t{rootSql}
            ORDER BY i.id DESC LIMIT $l OFFSET $o
            """;
        Bind(cmd, ps);
        cmd.Parameters.AddWithValue("$t", tagId);
        cmd.Parameters.AddWithValue("$l", limit);
        cmd.Parameters.AddWithValue("$o", offset);
        return ReadItems(cmd);
    }

    public int TagPhotoCount(long tagId)
    {
        using var conn = Open();
        if (!HasIt(conn)) return 0;
        var ps = new List<(string, object?)>();
        var rootSql = RootFilterSql(ps, "i.path");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(DISTINCT it.image_id) FROM it.image_tags it JOIN it.images i ON i.id=it.image_id WHERE it.tag_id = $t{rootSql}";
        Bind(cmd, ps);
        cmd.Parameters.AddWithValue("$t", tagId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>
    /// 某张图在标签相册里的 1-based 名次（0 = 不在结果中）。
    /// 排序必须与 <see cref="TagPhotos"/> 一致（i.id DESC），供查看器左右导航定位。
    /// </summary>
    public int TagPhotoRank(long tagId, string path)
    {
        if (tagId <= 0 || string.IsNullOrEmpty(path)) return 0;
        using var conn = Open();
        if (!HasIt(conn)) return 0;
        var ps = new List<(string, object?)>();
        var rootSql = RootFilterSql(ps, "i.path");

        using (var c0 = conn.CreateCommand())
        {
            c0.CommandText = $"SELECT COUNT(*) FROM it.image_tags it JOIN it.images i ON i.id=it.image_id WHERE it.tag_id=$t AND i.path=$path{rootSql}";
            Bind(c0, ps);
            c0.Parameters.AddWithValue("$t", tagId);
            c0.Parameters.AddWithValue("$path", path);
            if (Convert.ToInt32(c0.ExecuteScalar() ?? 0) == 0) return 0;
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT COUNT(DISTINCT i.id) FROM it.image_tags it JOIN it.images i ON i.id=it.image_id
            WHERE it.tag_id=$t{rootSql}
              AND i.id > (SELECT id FROM it.images WHERE path=$path LIMIT 1)
            """;
        Bind(cmd, ps);
        cmd.Parameters.AddWithValue("$t", tagId);
        cmd.Parameters.AddWithValue("$path", path);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0) + 1;
    }

    /// <summary>证件照类标签关键词（命中标签名 / tags 字段 / 描述）。</summary>
    public static readonly string[] IdPhotoKeywords =
    {
        "证件", "证件照", "身份证", "身份", "居民证", "正式证件", "手持证件",
        "行驶证", "驾驶证", "房产证", "护照", "证书", "光荣证",
    };

    /// <summary>文档类标签关键词。</summary>
    public static readonly string[] DocKeywords =
    {
        "文档", "文件", "纸质文件", "文字文件", "历史文件", "文件册",
        "通知书", "任命书", "文献", "证明", "资料",
    };

    /// <summary>
    /// 构造「标签族关键词」的 WHERE 条件（tags / description / 关联标签名 三者命中任一）。
    /// 与 <see cref="PhotosByTagKeywords"/>、<see cref="CountByTagKeywords"/>、<see cref="KeywordRank"/>
    /// 共用，保证三者的筛选口径完全一致（否则查看器导航会对不上页码）。
    /// </summary>
    private static string BuildKeywordWhere(string[] keywords, List<(string, object?)> ps)
    {
        var likes = new List<string>();
        for (int i = 0; i < keywords.Length; i++)
        {
            var n = "$k" + i;
            ps.Add((n, "%" + keywords[i] + "%"));
            likes.Add($"COALESCE(i.tags,'') LIKE {n}");
            likes.Add($"COALESCE(i.description,'') LIKE {n}");
            likes.Add($"EXISTS (SELECT 1 FROM it.image_tags x JOIN it.tags t ON t.id=x.tag_id WHERE x.image_id=i.id AND t.name LIKE {n})");
        }
        return string.Join(" OR ", likes);
    }

    /// <summary>
    /// 某张图在关键词结果里的 1-based 名次（0 = 不在结果中）。
    /// 排序必须与 PhotosByTagKeywords 一致（i.id DESC），供查看器左右导航定位（0.62.4）。
    /// </summary>
    public int KeywordRank(string[] keywords, string path)
    {
        if (keywords == null || keywords.Length == 0 || string.IsNullOrEmpty(path)) return 0;
        using var conn = Open();
        if (!HasIt(conn)) return 0;
        var ps = new List<(string, object?)>();
        var rootSql = RootFilterSql(ps, "i.path");
        var where = BuildKeywordWhere(keywords, ps);

        // ① 先确认这张图本身在结果集内；不在就直接返回 0，
        //    否则下面的「id 比它大的有几条」会算出一个并不存在的名次
        using (var c0 = conn.CreateCommand())
        {
            c0.CommandText = $"SELECT COUNT(*) FROM it.images i WHERE i.path=$p AND ({where}){rootSql}";
            Bind(c0, ps);
            c0.Parameters.AddWithValue("$p", path);
            if (Convert.ToInt32(c0.ExecuteScalar() ?? 0) == 0) return 0;
        }

        // ② 排序与 PhotosByTagKeywords 一致（i.id DESC）→ 名次 = 排在它前面的条数 + 1
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT COUNT(*) FROM it.images i
            WHERE ({where}){rootSql}
              AND i.id > (SELECT id FROM it.images WHERE path=$p LIMIT 1)
            """;
        Bind(cmd, ps);
        cmd.Parameters.AddWithValue("$p", path);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0) + 1;
    }

    /// <summary>按标签族关键词查图片（证件照 / 文档专用页）。仅识别库有标签语义。</summary>
    public List<ImgItem> PhotosByTagKeywords(string[] keywords, int offset, int limit)
    {
        if (keywords == null || keywords.Length == 0) return new List<ImgItem>();
        using var conn = Open();
        if (!HasIt(conn)) return new List<ImgItem>();
        var ps = new List<(string, object?)>();
        var rootSql = RootFilterSql(ps, "i.path");
        var likes = BuildKeywordWhere(keywords, ps);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT i.path, i.filename, i.width, i.height, i.file_size, i.description, i.tags, i.id AS ord, 'imgtag' AS src, i.faces_scanned
            FROM it.images i
            WHERE ({string.Join(" OR ", likes)}){rootSql}
            ORDER BY i.id DESC LIMIT $l OFFSET $o
            """;
        Bind(cmd, ps);
        cmd.Parameters.AddWithValue("$l", limit);
        cmd.Parameters.AddWithValue("$o", offset);
        return ReadItems(cmd);
    }

    public int CountByTagKeywords(string[] keywords)
    {
        if (keywords == null || keywords.Length == 0) return 0;
        using var conn = Open();
        if (!HasIt(conn)) return 0;
        var ps = new List<(string, object?)>();
        var rootSql = RootFilterSql(ps, "i.path");
        var likes = BuildKeywordWhere(keywords, ps);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM it.images i WHERE ({likes}){rootSql}";
        Bind(cmd, ps);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>人脸识别扫描统计：总图片数、已识别数、人脸总数</summary>
    public sealed class FaceScanStats
    {
        public int Total;
        public int Scanned;
        public int Faces;
    }

    /// <summary>获取人脸识别统计（仅统计识别库中的图片）</summary>
    public FaceScanStats GetFaceScanStats()
    {
        var stats = new FaceScanStats();
        using var conn = Open();
        if (!HasIt(conn)) return stats;
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM it.images) AS total,
                    (SELECT COUNT(*) FROM it.images WHERE faces_scanned > 0) AS scanned,
                    (SELECT COUNT(*) FROM it.image_faces) AS faces
                """;
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                stats.Total = r.GetInt32(0);
                stats.Scanned = r.GetInt32(1);
                stats.Faces = r.GetInt32(2);
            }
        }
        catch { }
        return stats;
    }

    /// <summary>图片人脸框（bbox 字符串 "x1,y1,x2,y2"，返回相对比例 0-1）</summary>
    public List<FaceBox> FacesByPath(string path)
    {
        using var conn = Open();
        if (!HasIt(conn)) return new List<FaceBox>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT f.bbox, i.width, i.height, p.name
            FROM it.image_faces f
            JOIN it.images i ON i.id = f.image_id
            LEFT JOIN it.persons p ON p.id = f.person_id
            WHERE i.path = $p
              AND (p.id IS NULL OR ifnull(p.ignored, 0) = 0)   -- 已忽略人物不画框
            """;
        cmd.Parameters.AddWithValue("$p", path);
        var list = new List<FaceBox>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var bbox = r.IsDBNull(0) ? "" : r.GetString(0);
            var w = r.GetInt32(1);
            var h = r.GetInt32(2);
            var name = r.IsDBNull(3) ? "" : r.GetString(3);
            var parts = bbox.Split(',');
            if (parts.Length == 4 && w > 0 && h > 0 &&
                double.TryParse(parts[0], out var x1) && double.TryParse(parts[1], out var y1) &&
                double.TryParse(parts[2], out var x2) && double.TryParse(parts[3], out var y2))
            {
                list.Add(new FaceBox
                {
                    X = x1 / w, Y = y1 / h, Width = (x2 - x1) / w, Height = (y2 - y1) / h, Name = name
                });
            }
        }
        return list;
    }

    private static List<ImgItem> ReadItems(SqliteCommand cmd)
    {
        var list = new List<ImgItem>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new ImgItem
            {
                Path = r.GetString(0),
                Filename = r.GetString(1),
                Width = r.GetInt32(2),
                Height = r.GetInt32(3),
                FileSize = r.GetInt64(4),
                Description = r.IsDBNull(5) ? "" : r.GetString(5),
                Tags = r.IsDBNull(6) ? "" : r.GetString(6),
                Source = r.IsDBNull(8) ? "folder" : (r.GetInt32(8) == 1 ? "imgtag" : "folder"),
                FaceScanned = !r.IsDBNull(9) && r.GetInt32(9) > 0
            });
        }
        return list;
    }

    // ==================== 快速读图尺寸（不解码全图） ====================

    public static (int Width, int Height) FastImageSize(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var head = new byte[32];
            int read = fs.Read(head, 0, 32);
            if (read < 8) return (0, 0);
            // PNG
            if (head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47)
                return (head[16] << 24 | head[17] << 16 | head[18] << 8 | head[19], head[20] << 24 | head[21] << 16 | head[22] << 8 | head[23]);
            // GIF
            if (head[0] == 0x47 && head[1] == 0x49 && head[2] == 0x46)
                return (head[6] | head[7] << 8, head[8] | head[9] << 8);
            // BMP
            if (head[0] == 0x42 && head[1] == 0x4D)
                return (head[18] | head[19] << 8 | head[20] << 16 | head[21] << 24, head[22] | head[23] << 8 | head[24] << 16 | head[25] << 24);
            // JPEG：扫描 SOF0/SOF2
            if (head[0] == 0xFF && head[1] == 0xD8)
            {
                using var jfs = File.OpenRead(path);
                var buf = new byte[1024 * 64];
                int pos = 2;
                while (pos < jfs.Length)
                {
                    jfs.Position = pos;
                    int n = jfs.Read(buf, 0, buf.Length);
                    if (n < 4) break;
                    for (int i = 0; i < n - 8; i++)
                    {
                        if (buf[i] == 0xFF && (buf[i + 1] & 0xF0) == 0xC0 && buf[i + 1] != 0xC4 && buf[i + 1] != 0xC8 && buf[i + 1] != 0xCC)
                        {
                            return (buf[i + 5] << 8 | buf[i + 6], buf[i + 3] << 8 | buf[i + 4]);
                        }
                    }
                    pos += n - 4;
                    if (pos > jfs.Length) break;
                }
            }
            // WebP（VP8X/VP8/VP8L 简易）
            if (head[0] == 0x52 && head[1] == 0x49 && head[2] == 0x46 && head[3] == 0x46 && head[8] == 0x57 && head[9] == 0x45 && head[10] == 0x42 && head[11] == 0x50)
            {
                if (head[12] == 0x56 && head[13] == 0x50 && head[14] == 0x38 && head[15] == 0x58)
                    return (((head[24] | head[25] << 8 | head[26] << 16) & 0x3FFF) + 1, ((head[27] | head[28] << 8 | head[29] << 16) & 0x3FFF) + 1);
            }
        }
        catch { }
        return (0, 0);
    }
}

/// <summary>「建议合并」候选：两个人物的人脸特征高度相似，可能其实是同一个人</summary>
public sealed class MergeSuggestion : System.ComponentModel.INotifyPropertyChanged
{
    public long PersonIdA { get; set; }
    public long PersonIdB { get; set; }
    public string NameA { get; set; } = "";
    public string NameB { get; set; } = "";
    public int CountA { get; set; }
    public int CountB { get; set; }
    public long? AvatarFaceIdA { get; set; }
    public long? AvatarFaceIdB { get; set; }
    public float Similarity { get; set; }

    public string SimText => $"{Similarity * 100:F0}%";
    public string CountTextA => $"{CountA} 张";
    public string CountTextB => $"{CountB} 张";
    /// <summary>合并方向说明：把 B 并入 A</summary>
    public string MergeText => $"「{NameB}」并入「{NameA}」";

    private bool _isChecked;
    /// <summary>复选框绑定；必须通知 UI，否则勾选后不会有任何视觉反馈</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsChecked
    {
        get => _isChecked;
        set { _isChecked = value; Raise(nameof(IsChecked)); }
    }

    private System.Windows.Media.ImageSource? _thumbA;
    [System.Text.Json.Serialization.JsonIgnore]
    public System.Windows.Media.ImageSource? ThumbA
    {
        get => _thumbA;
        set { _thumbA = value; Raise(nameof(ThumbA)); }
    }

    private System.Windows.Media.ImageSource? _thumbB;
    [System.Text.Json.Serialization.JsonIgnore]
    public System.Windows.Media.ImageSource? ThumbB
    {
        get => _thumbB;
        set { _thumbB = value; Raise(nameof(ThumbB)); }
    }

    private void Raise(string name)
        => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>人物条目</summary>
public class PersonItem : System.ComponentModel.INotifyPropertyChanged
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public int PhotoCount { get; set; }
    public string SamplePath { get; set; } = "";
    /// <summary>指定的封面人脸（avatar_face_id）；为空时回退到首张人脸原图</summary>
    public long? AvatarFaceId { get; set; }

    public string PhotoCountText => $"{PhotoCount} 张";

    private System.Windows.Media.ImageSource? _thumb;
    [System.Text.Json.Serialization.JsonIgnore]
    public System.Windows.Media.ImageSource? Thumb
    {
        get => _thumb;
        set { _thumb = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Thumb))); }
    }

    private bool _isSelected;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSelected))); }
    }

    private bool _multiMode;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool MultiMode
    {
        get => _multiMode;
        set { _multiMode = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(MultiMode))); }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>人物的一张人脸（换封面候选）</summary>
public class PersonFaceItem
{
    public long FaceId { get; set; }
    public long ImageId { get; set; }
    public string Bbox { get; set; } = "";
    public double Score { get; set; }
    public string ThumbUrl => $"{ImgtagRecognizer.ServerUrl}/api/face-thumbnail/{FaceId}";
    public string ScoreText => $"置信 {Score * 100:F0}%";
}

/// <summary>标签条目</summary>
public class TagItem
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public int Count { get; set; }

    public string CountText => Count.ToString();
}

/// <summary>人脸框（相对比例 0-1）</summary>
public class FaceBox
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public string Name { get; set; } = "";
}
