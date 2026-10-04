using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace PictureButler;

/// <summary>
/// 本地 HTTP 服务（127.0.0.1，TcpListener 手写极简 HTTP，无需 URL ACL/管理员权限）。
/// 浏览器扩展通过它获取星标提示词列表；同时为程序自身提供人脸缩略图端点
/// （原先由独立 imgtag 服务的 8520 端口提供，现全部收敛到本进程内）。
/// API：
///   GET /api/health          → {"ok":true,"app":"PictureButler","version":"0.62.10"}
///   GET /api/starred         → [{"id","title","content","updatedAt"}, ...]
///   GET /api/prompts?q=      → 搜索提示词
///   GET /api/face-thumbnail/{faceId} → 人脸裁剪缩略图（JPEG，进程内生成/缓存）
/// </summary>
public class LocalHttpServer : IDisposable
{
    private TcpListener? _listener;
    private CancellationTokenSource _cts = new();
    private DataService _db = null!;
    private Func<List<PromptItem>>? _itemsProvider;

    /// <summary>实际监听的端口（被占用时自动递增）</summary>
    public int Port { get; private set; }

    /// <summary>
    /// 服务基址（如 http://127.0.0.1:8189）。供 <see cref="ImgtagRecognizer.ServerUrl"/>
    /// 与人物封面绑定使用——不再指向独立的 8520 服务。
    /// </summary>
    public static string BaseUrl { get; private set; } = "http://127.0.0.1:8189";

    /// <summary>
    /// 人脸缩略图提供者：入参 faceId，返回 JPEG 字节（null = 取不到）。
    /// 由 MainWindow 挂接为「进程内生成（imgtag_native）+ 读缓存文件」。
    /// </summary>
    public static Func<long, byte[]?>? FaceThumbnailProvider;

    private const string FaceThumbPrefix = "/api/face-thumbnail/";

    public void Start(DataService db, Func<List<PromptItem>> itemsProvider, int startPort = 8189)
    {
        _db = db;
        _itemsProvider = itemsProvider;
        for (int p = startPort; p < startPort + 20; p++)
        {
            try
            {
                _listener = new TcpListener(IPAddress.Loopback, p);
                _listener.Start();
                Port = p;
                break;
            }
            catch { }
        }
        if (_listener == null) return;
        BaseUrl = $"http://127.0.0.1:{Port}";
        _ = Task.Run(ServerLoop);
    }

    private async Task ServerLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(_cts.Token);
            }
            catch
            {
                break;
            }
            _ = Task.Run(() => Handle(client));
        }
    }

    private async Task Handle(TcpClient client)
    {
        try
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 8192, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync();
            if (requestLine == null) return;
            var parts = requestLine.Split(' ');
            var method = parts.Length > 0 ? parts[0] : "GET";
            var target = parts.Length > 1 ? parts[1] : "/";
            // 读完请求头
            while (true)
            {
                var line = await reader.ReadLineAsync();
                if (string.IsNullOrEmpty(line)) break;
            }

            var path = target.Split('?')[0];

            // 人脸缩略图端点：进程内生成/缓存后返回 JPEG 字节
            if (path.StartsWith(FaceThumbPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var (st, ct, bd) = RouteFaceThumbnail(path);
                await WriteBinaryAsync(stream, st, ct, bd);
                return;
            }

            // 图片端点：返回二进制（图片 dataUrl 解码）
            if (path == "/api/image")
            {
                var (status2, contentType, body2) = RouteImage(method, target);
                await WriteBinaryAsync(stream, status2, contentType, body2);
                return;
            }

            var (status, body) = Route(method, target);
            var respBody = Encoding.UTF8.GetBytes(body);
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(status).Append("\r\n");
            sb.Append("Content-Type: application/json; charset=utf-8\r\n");
            sb.Append("Access-Control-Allow-Origin: *\r\n");
            sb.Append("Access-Control-Allow-Methods: GET, OPTIONS\r\n");
            sb.Append("Content-Length: ").Append(respBody.Length).Append("\r\n");
            sb.Append("Connection: close\r\n\r\n");
            var head = Encoding.UTF8.GetBytes(sb.ToString());
            await stream.WriteAsync(head);
            await stream.WriteAsync(respBody);
        }
        catch { }
        finally
        {
            client.Dispose();
        }
    }

    /// <summary>写二进制响应（带 CORS 头，Connection: close）</summary>
    private static async Task WriteBinaryAsync(NetworkStream stream, string status, string contentType, byte[] body)
    {
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(status).Append("\r\n");
        sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
        sb.Append("Access-Control-Allow-Origin: *\r\n");
        sb.Append("Access-Control-Allow-Methods: GET, OPTIONS\r\n");
        sb.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        sb.Append("Connection: close\r\n\r\n");
        await stream.WriteAsync(Encoding.UTF8.GetBytes(sb.ToString()));
        if (body.Length > 0) await stream.WriteAsync(body);
    }

    /// <summary>
    /// 人脸缩略图端点：/api/face-thumbnail/{faceId}
    /// 交由 <see cref="FaceThumbnailProvider"/>（进程内 imgtag_native 生成 + 缓存），
    /// 取代原先独立 imgtag 服务 8520 上的同名端点。
    /// </summary>
    private static (string status, string contentType, byte[] body) RouteFaceThumbnail(string path)
    {
        var seg = path[FaceThumbPrefix.Length..];
        var slash = seg.IndexOf('/');
        if (slash >= 0) seg = seg[..slash];
        if (!long.TryParse(seg, out var faceId) || faceId <= 0)
            return ("400 Bad Request", "application/json", Encoding.UTF8.GetBytes("{\"error\":\"bad face id\"}"));

        try
        {
            var bytes = FaceThumbnailProvider?.Invoke(faceId);
            if (bytes != null && bytes.Length > 0)
                return ("200 OK", "image/jpeg", bytes);
        }
        catch { }
        return ("404 Not Found", "application/json", Encoding.UTF8.GetBytes("{\"error\":\"no thumbnail\"}"));
    }

    /// <summary>图片端点：/api/image?id=&amp;kind=preview|gallery_0 — 返回图片字节</summary>
    private (string status, string contentType, byte[] body) RouteImage(string method, string target)
    {
        if (method != "GET")
            return ("405 Method Not Allowed", "application/json", Encoding.UTF8.GetBytes("{\"error\":\"method not allowed\"}"));

        var query = target.Contains('?') ? target[(target.IndexOf('?') + 1)..] : "";
        var id = GetQueryParam(query, "id");
        var kind = GetQueryParam(query, "kind");
        if (string.IsNullOrEmpty(id)) return ("400 Bad Request", "application/json", Encoding.UTF8.GetBytes("{\"error\":\"missing id\"}"));

        string? dataUrl = null;
        try
        {
            if (string.IsNullOrEmpty(kind) || kind == "preview")
                dataUrl = _db.LoadPreviewImage(id);
            else if (kind.StartsWith("gallery_", StringComparison.OrdinalIgnoreCase) &&
                     int.TryParse(kind["gallery_".Length..], out var idx))
            {
                var gallery = _db.LoadGalleryImages(id);
                if (idx >= 0 && idx < gallery.Count) dataUrl = gallery[idx];
            }
        }
        catch { }

        if (string.IsNullOrEmpty(dataUrl) || !dataUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return ("404 Not Found", "application/json", Encoding.UTF8.GetBytes("{\"error\":\"no image\"}"));

        var comma = dataUrl.IndexOf(',');
        if (comma <= 0) return ("404 Not Found", "application/json", Encoding.UTF8.GetBytes("{\"error\":\"bad dataUrl\"}"));
        var meta = dataUrl[..comma];
        var contentType = meta.Contains("image/webp") ? "image/webp"
            : meta.Contains("image/jpeg") ? "image/jpeg"
            : meta.Contains("image/png") ? "image/png"
            : "application/octet-stream";
        try
        {
            var bytes = Convert.FromBase64String(dataUrl[(comma + 1)..]);
            return ("200 OK", contentType, bytes);
        }
        catch
        {
            return ("404 Not Found", "application/json", Encoding.UTF8.GetBytes("{\"error\":\"bad base64\"}"));
        }
    }

    private (string status, string body) Route(string method, string target)
    {
        var path = target.Split('?')[0];
        var query = target.Contains('?') ? target[(target.IndexOf('?') + 1)..] : "";

        if (method == "OPTIONS")
            return ("204 No Content", "");

        if (method != "GET")
            return ("405 Method Not Allowed", "{\"error\":\"method not allowed\"}");

        switch (path)
        {
            case "/api/health":
                return ("200 OK", "{\"ok\":true,\"app\":\"PictureButler\",\"version\":\"0.62.10\"}");

            case "/api/starred":
            {
                var items = _itemsProvider?.Invoke() ?? _db.LoadItems();
                var starred = items.Where(i => i.Starred)
                                   .OrderByDescending(i => ParseDate(i.UpdatedAt))
                                   .Select(i => new
                                   {
                                       id = i.Id,
                                       title = i.Title,
                                       content = i.Content,
                                       updatedAt = i.UpdatedAt,
                                       hasImage = i.HasPreviewImage == true || i.HasGalleryImages == true
                                   })
                                   .ToList();
                return ("200 OK", JsonSerializer.Serialize(starred));
            }

            case "/api/prompts":
            {
                var items = _itemsProvider?.Invoke() ?? _db.LoadItems();
                var q = GetQueryParam(query, "q")?.Trim().ToLowerInvariant();
                IEnumerable<PromptItem> filtered = items;
                if (!string.IsNullOrEmpty(q))
                {
                    filtered = filtered.Where(i =>
                        i.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        i.Content.Contains(q, StringComparison.OrdinalIgnoreCase));
                }
                var result = filtered.OrderByDescending(i => ParseDate(i.UpdatedAt))
                                     .Take(200)
                                     .Select(i => new
                                     {
                                         id = i.Id,
                                         title = i.Title,
                                         content = i.Content,
                                         starred = i.Starred,
                                         updatedAt = i.UpdatedAt
                                     })
                                     .ToList();
                return ("200 OK", JsonSerializer.Serialize(result));
            }

            default:
                return ("404 Not Found", "{\"error\":\"not found\"}");
        }
    }

    private static string? GetQueryParam(string query, string name)
    {
        foreach (var pair in query.Split('&'))
        {
            var kv = pair.Split('=');
            if (kv.Length == 2 && kv[0] == name)
                return Uri.UnescapeDataString(kv[1]);
        }
        return null;
    }

    private static DateTime ParseDate(string? iso)
        => DateTime.TryParse(iso, out var dt) ? dt : DateTime.MinValue;

    public void Dispose()
    {
        _cts.Cancel();
        _listener?.Stop();
        _cts.Dispose();
    }
}
