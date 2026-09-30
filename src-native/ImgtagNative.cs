using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace PictureButler;

/// <summary>
/// imgtag 识别引擎的**进程内** FFI 封装（Rust cdylib：<c>imgtag_native.dll</c>）。
///
/// 取代原先「独立 imgtag.exe + HTTP 127.0.0.1:8520」形态：
///   · 人脸识别 / AI 打标 / 人脸缩略图 全部在 PictureButler 进程内完成；
///   · 无独立进程、无 8520 端口；
///   · 数据仍是同一份 <c>db.sqlite</c>（由 Rust 侧打开），旧人脸与标签数据零迁移。
///
/// 说明：字符串统一用 UTF-8（<c>MarshalAs(UnmanagedType.LPUTF8Str)</c>），
/// 数据交换统一用 JSON，返回的堆字符串由 <c>pb_imgtag_free_string</c> 释放。
/// </summary>
internal static class ImgtagNative
{
    private const string DllName = "imgtag_native";

    /// <summary>进度回调：<paramref name="ctx"/> 为 GCHandle 句柄；<paramref name="jsonUtf8"/> 为事件 JSON。</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void ProgressCallback(IntPtr ctx, IntPtr jsonUtf8);

    private static string? _searchDir;

    // ---- 原生函数声明 --------------------------------------------------------

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int pb_imgtag_init(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string baseDir,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string dbPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string ortDylib,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string lmUrl,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string lmModel,
        out IntPtr errOut);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int pb_imgtag_health();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int pb_imgtag_cancel();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int pb_imgtag_shutdown();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr pb_imgtag_scan_faces(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string requestJson,
        IntPtr ctx,
        ProgressCallback? progress);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr pb_imgtag_ai_tag(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string requestJson,
        IntPtr ctx,
        ProgressCallback? progress);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr pb_imgtag_ensure_face_thumbnail(long faceId);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void pb_imgtag_free_string(IntPtr s);

    // ---- DLL 解析：优先从 imgtag 数据目录加载 ---------------------------------

    /// <summary>指定 imgtag 数据目录（<c>imgtag_native.dll</c> / <c>onnxruntime.dll</c> 所在处）。</summary>
    public static void SetDataDirectory(string? dir)
    {
        if (!string.IsNullOrWhiteSpace(dir)) _searchDir = dir;
    }

    /// <summary>最近一次预检结论（0.62.9）。UI 点「识别人脸 / AI 打标」时若引擎不可用，用它给明确原因。</summary>
    public static string? LastPreflightMessage { get; private set; }
    public static bool PreflightOk { get; private set; }

    /// <summary>
    /// 启动预检（0.62.7 / 0.62.9 加强）：查文件 + **试加载 DLL + 解析导出符号**，
    /// 但不初始化引擎、不加载 ONNX 模型（保持秒开）。
    /// 失败时返回明确中文原因；提示词等功能照常可用。
    /// </summary>
    public static (bool Ok, string Message) Preflight(string? dataDir)
    {
        var dir = dataDir;
        if (string.IsNullOrWhiteSpace(dir))
            dir = Path.Combine(AppContext.BaseDirectory, "imgtag");
        var missing = new List<string>();

        var native = Path.Combine(dir, "imgtag_native.dll");
        if (!File.Exists(native)) missing.Add("imgtag_native.dll");

        var ort = Path.Combine(dir, "onnxruntime.dll");
        if (!File.Exists(ort)) missing.Add("onnxruntime.dll");

        var models = Path.Combine(dir, "models");
        // ONNX 模型在 models\ 下的子目录里（如 buffalo_l\），必须递归看
        if (!Directory.Exists(models) ||
            !Directory.EnumerateFileSystemEntries(models, "*", SearchOption.AllDirectories).Any())
            missing.Add("models\\（ONNX 模型）");

        if (missing.Count > 0)
        {
            PreflightOk = false;
            LastPreflightMessage =
                $"识别引擎文件不完整，人脸识别 / AI 打标暂不可用。缺少：{string.Join("、", missing)}。" +
                $"目录：{dir}。请确认 imgtag 文件夹完整后重启程序。";
            AppLogger.Warn($"imgtag preflight FAILED  dir={dir}  missing={string.Join(",", missing)}");
            return (false, LastPreflightMessage);
        }

        // 文件齐了还要能真加载：架构不对 / DLL 损坏 / 缺导出 都会在这里现形，
        // 而不是等到第一次 P/Invoke 时抛 DllNotFoundException（恰恰是预检要防的场景）。
        // 只 TryLoad + 查符号，不调 pb_imgtag_init，毫秒级。
        if (!NativeLibrary.TryLoad(native, out var handle))
        {
            PreflightOk = false;
            LastPreflightMessage =
                $"imgtag_native.dll 无法加载（可能架构不匹配或文件损坏）：{native}。人脸识别 / AI 打标暂不可用。";
            AppLogger.Warn($"imgtag preflight FAILED  TryLoad failed  {native}");
            return (false, LastPreflightMessage);
        }
        try
        {
            if (!NativeLibrary.TryGetExport(handle, "pb_imgtag_health", out _))
            {
                PreflightOk = false;
                LastPreflightMessage =
                    $"imgtag_native.dll 缺少导出符号 pb_imgtag_health（文件版本不对）：{native}。人脸识别 / AI 打标暂不可用。";
                AppLogger.Warn($"imgtag preflight FAILED  missing export  {native}");
                return (false, LastPreflightMessage);
            }
        }
        finally
        {
            // 释放探针句柄；真正使用时由 DllImportResolver 重新加载
            try { NativeLibrary.Free(handle); } catch { }
        }

        PreflightOk = true;
        LastPreflightMessage = null;
        AppLogger.Info($"imgtag preflight OK  dir={dir}  (TryLoad + export verified)");
        return (true, "识别引擎就绪");
    }

    static ImgtagNative()
    {
        try { NativeLibrary.SetDllImportResolver(typeof(ImgtagNative).Assembly, Resolve); }
        catch { /* 已被其他类型注册过则忽略 */ }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, DllName, StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero;
        foreach (var dir in CandidateDirs())
        {
            try
            {
                var path = Path.Combine(dir, DllName + ".dll");
                if (File.Exists(path) && NativeLibrary.TryLoad(path, out var h))
                    return h;
            }
            catch { }
        }
        return IntPtr.Zero;   // 回退系统默认解析
    }

    private static IEnumerable<string> CandidateDirs()
    {
        if (!string.IsNullOrEmpty(_searchDir)) yield return _searchDir!;
        yield return AppContext.BaseDirectory;
        yield return Path.Combine(AppContext.BaseDirectory, "imgtag");
    }

    // ---- 结果模型 ------------------------------------------------------------

    public sealed class NativeResult
    {
        public bool Ok;
        public string Error = "";
        public int Scanned;
        public int Failed;
        public int TotalFaces;
        public int Requested;
        public string Path = "";
    }

    // ---- 对外 API ------------------------------------------------------------

    /// <summary>初始化识别引擎（幂等）。<paramref name="baseDir"/> 应含 models/、.faces/、onnxruntime.dll。</summary>
    public static bool Initialize(string baseDir, string dbPath, string? lmUrl, string? lmModel, out string error)
    {
        error = "";
        try
        {
            var ort = Path.Combine(baseDir ?? "", "onnxruntime.dll");
            var rc = pb_imgtag_init(
                baseDir ?? "",
                dbPath ?? "",
                File.Exists(ort) ? ort : "",
                lmUrl ?? "",
                lmModel ?? "",
                out var errPtr);
            var msg = TakeAndFree(errPtr);
            if (rc != 0)
            {
                error = string.IsNullOrEmpty(msg) ? $"识别引擎初始化失败（代码 {rc}）" : msg;
                return false;
            }
            return true;
        }
        catch (DllNotFoundException)
        {
            error = $"未找到 {DllName}.dll（查找目录：{_searchDir ?? "程序目录"}）";
            return false;
        }
        catch (Exception ex)
        {
            error = "初始化识别引擎失败：" + ex.Message;
            return false;
        }
    }

    /// <summary>引擎是否已就绪</summary>
    public static bool IsReady()
    {
        try { return pb_imgtag_health() == 1; }
        catch { return false; }
    }

    /// <summary>请求取消当前识别任务（在图片之间生效）</summary>
    public static void Cancel()
    {
        try { pb_imgtag_cancel(); } catch { }
    }

    /// <summary>释放识别引擎（程序退出前调用）</summary>
    public static void Shutdown()
    {
        try { pb_imgtag_shutdown(); } catch { }
    }

    /// <summary>批量人脸识别。<paramref name="requestJson"/>：{"folder":"","image_ids":"1,2","force":false}</summary>
    public static NativeResult ScanFaces(string requestJson, Action<string>? progress)
        => RunWithProgress(requestJson, progress, isScan: true);

    /// <summary>批量 AI 打标。<paramref name="requestJson"/>：{"folder":"","image_ids":"1,2","limit":200}</summary>
    public static NativeResult AiTag(string requestJson, Action<string>? progress)
        => RunWithProgress(requestJson, progress, isScan: false);

    /// <summary>确保某张人脸的裁剪缩略图存在，返回其文件路径（失败返回 null）。</summary>
    public static string? EnsureFaceThumbnail(long faceId)
    {
        try
        {
            var res = new NativeResult();
            ParseResult(pb_imgtag_ensure_face_thumbnail(faceId), res);
            return res.Ok ? res.Path : null;
        }
        catch { return null; }
    }

    // ---- 内部实现 ------------------------------------------------------------

    private sealed class ProgressSink
    {
        public Action<string>? Sink;
    }

    private static readonly ProgressCallback ProgressTrampoline = OnProgress;

    private static NativeResult RunWithProgress(string requestJson, Action<string>? progress, bool isScan)
    {
        var res = new NativeResult();
        IntPtr ptr;
        if (progress == null)
        {
            try
            {
                ptr = isScan
                    ? pb_imgtag_scan_faces(requestJson, IntPtr.Zero, null)
                    : pb_imgtag_ai_tag(requestJson, IntPtr.Zero, null);
            }
            catch (DllNotFoundException) { res.Error = $"未找到 {DllName}.dll"; return res; }
            catch (Exception ex) { res.Error = "调用识别引擎失败：" + ex.Message; return res; }
            ParseResult(ptr, res);
            return res;
        }

        var box = new ProgressSink { Sink = progress };
        var handle = GCHandle.Alloc(box);
        try
        {
            ptr = isScan
                ? pb_imgtag_scan_faces(requestJson, GCHandle.ToIntPtr(handle), ProgressTrampoline)
                : pb_imgtag_ai_tag(requestJson, GCHandle.ToIntPtr(handle), ProgressTrampoline);
        }
        catch (DllNotFoundException) { res.Error = $"未找到 {DllName}.dll"; return res; }
        catch (Exception ex) { res.Error = "调用识别引擎失败：" + ex.Message; return res; }
        finally
        {
            GC.KeepAlive(ProgressTrampoline);
            if (handle.IsAllocated) handle.Free();
        }
        ParseResult(ptr, res);
        return res;
    }

    /// <summary>回调桩：把事件 JSON 原样交给上层（与旧 SSE data 行同构），绝不抛异常穿回原生栈。</summary>
    private static void OnProgress(IntPtr ctx, IntPtr jsonUtf8)
    {
        try
        {
            if (ctx == IntPtr.Zero || jsonUtf8 == IntPtr.Zero) return;
            if (GCHandle.FromIntPtr(ctx).Target is not ProgressSink box) return;
            var json = Marshal.PtrToStringUTF8(jsonUtf8);
            if (string.IsNullOrEmpty(json)) return;
            box.Sink?.Invoke(json);
        }
        catch { }
    }

    private static void ParseResult(IntPtr ptr, NativeResult res)
    {
        var json = TakeAndFree(ptr);
        if (string.IsNullOrEmpty(json))
        {
            res.Ok = false;
            if (string.IsNullOrEmpty(res.Error)) res.Error = "识别引擎无返回";
            return;
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            res.Ok = !root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.False;
            if (root.TryGetProperty("error", out var e)) res.Error = e.GetString() ?? "";
            if (root.TryGetProperty("scanned", out var s)) res.Scanned = s.GetInt32();
            if (root.TryGetProperty("failed", out var f)) res.Failed = f.GetInt32();
            if (root.TryGetProperty("total_faces", out var tf)) res.TotalFaces = tf.GetInt32();
            if (root.TryGetProperty("requested", out var rq)) res.Requested = rq.GetInt32();
            if (root.TryGetProperty("path", out var pa)) res.Path = pa.GetString() ?? "";
        }
        catch (Exception ex)
        {
            res.Ok = false;
            res.Error = "识别结果解析失败：" + ex.Message;
        }
    }

    /// <summary>取走原生返回的 UTF-8 堆字符串并释放。</summary>
    private static string TakeAndFree(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return "";
        string s;
        try { s = Marshal.PtrToStringUTF8(ptr) ?? ""; }
        catch { s = ""; }
        finally { try { pb_imgtag_free_string(ptr); } catch { } }
        return s;
    }
}
