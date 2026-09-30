using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace PictureButler;

/// <summary>
/// ComfyUI 生成自动记录器：轮询本地 ComfyUI /history，检测新生成的成功任务，
/// 提取正向/负向提示词与生成参数，下载输出图片，自动入库（新建或按内容合并）。
/// 逻辑移植自原扩展 comfyui-monitor.js（v4.9.4）。
/// </summary>
public class ComfyUiMonitor : IDisposable
{
    private readonly DataService _db;
    private readonly HttpClient _http;
    private string _baseUrl = "http://127.0.0.1:8188";
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private readonly HashSet<string> _knownIds = new();
    private readonly object _lock = new();

    /// <summary>状态回调（可能在线程池线程触发）</summary>
    public Action<string>? OnStatus;
    /// <summary>捕获入库回调</summary>
    public Action<PromptItem>? OnSaved;

    /// <summary>探测本机 ComfyUI 输出目录（常见安装位置），找不到返回 null</summary>
    public static string? DetectOutputDir()
    {
        string[] candidates =
        {
            @"D:\ComfyUI\ComfyUI\output",
            @"C:\ComfyUI\ComfyUI\output",
            @"D:\ComfyUI\output",
            @"C:\ComfyUI\output",
            @"E:\ComfyUI\ComfyUI\output",
            @"E:\ComfyUI\output",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ComfyUI", "output")
        };
        foreach (var dir in candidates)
        {
            try
            {
                if (Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.png").Any())
                    return dir;
            }
            catch { }
        }
        return null;
    }

    public ComfyUiMonitor(DataService db)
    {
        _db = db;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
    }

    public bool Running => _cts != null && !_cts.IsCancellationRequested;

    public void Start(string baseUrl, int intervalMs = 3000)
    {
        Stop();
        _baseUrl = (baseUrl ?? "").Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(_baseUrl)) _baseUrl = "http://127.0.0.1:8188";
        _cts = new CancellationTokenSource();
        // 快照在后台线程执行，避免阻塞调用线程（UI）
        _ = Task.Run(() =>
        {
            try { Snapshot(); }
            catch { }
        });
        _loop = Task.Run(() => LoopAsync(_cts.Token, intervalMs));
        OnStatus?.Invoke($"ComfyUI 监听已启动（{_baseUrl}）");
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        _cts = null;
        _loop = null;
    }

    // ===== 启动快照：已保存条目的 promptId + 当前 history 全部 pid =====
    private void Snapshot()
    {
        try
        {
            foreach (var item in _db.LoadItems())
            {
                if (item.GenerationInfo is JsonElement je &&
                    je.ValueKind == JsonValueKind.Object &&
                    je.TryGetProperty("promptId", out var pid) &&
                    pid.ValueKind == JsonValueKind.String)
                {
                    lock (_lock) _knownIds.Add(pid.GetString()!);
                }
            }
        }
        catch { }
        try
        {
            var hist = GetJson("/history?max_items=50").GetAwaiter().GetResult();
            if (hist is JsonElement h && h.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in h.EnumerateObject())
                    lock (_lock) _knownIds.Add(p.Name);
            }
        }
        catch { }
    }

    private async Task LoopAsync(CancellationToken ct, int interval)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await PollOnceAsync(ct); }
            catch { }
            await Task.Delay(interval, ct);
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        var hist = await GetJson("/history?max_items=30");
        var root = hist;
        if (root.ValueKind != JsonValueKind.Object) return;
        foreach (var prop in root.EnumerateObject())
        {
            if (ct.IsCancellationRequested) return;
            var pid = prop.Name;
            lock (_lock)
            {
                if (_knownIds.Contains(pid)) continue;
                _knownIds.Add(pid);
            }
            try { await ProcessAsync(pid, prop.Value, ct); }
            catch { }
        }
    }

    private async Task ProcessAsync(string pid, JsonElement entry, CancellationToken ct)
    {
        var status = entry.TryGetProperty("status", out var st) ? st : default;
        var statusStr = status.ValueKind == JsonValueKind.Object && status.TryGetProperty("status_str", out var ss)
            ? ss.GetString() : "";
        if (!string.Equals(statusStr, "success", StringComparison.OrdinalIgnoreCase)) return;

        var parsed = ExtractParams(entry);
        var positive = (parsed.Positive ?? "").Trim();
        if (positive.Length == 0) return;

        var (images, imageFiles) = await DownloadImages(entry, ct);

        // 查重：按正向提示词内容精确匹配（不同 prompt_id 的相同提示词合并）
        PromptItem? existing = null;
        try { existing = _db.LoadItems().FirstOrDefault(i => i.Content == positive); }
        catch { }

        PromptItem item;
        var genInfo = BuildGenerationInfo(pid, parsed, images.Count, imageFiles);
        if (existing != null)
        {
            // 合并：追加新图 + 更新 generationInfo（保留原有图片）
            var oldImages = new List<string>();
            try { oldImages = _db.LoadGalleryImages(existing.Id); }
            catch { }
            if (oldImages.Count == 0)
            {
                var oldPreview = _db.LoadPreviewImage(existing.Id);
                if (!string.IsNullOrEmpty(oldPreview)) oldImages.Add(oldPreview);
            }
            oldImages.AddRange(images);
            item = existing;
            item.GenerationInfo = MergeGenerationInfo(existing.GenerationInfo, genInfo);
            _db.SaveItem(item, false);
            if (oldImages.Count > 0) _db.SaveImages(existing.Id, oldImages);
            OnStatus?.Invoke($"已合并到「{existing.Title}」（新增 {images.Count} 张图）");
        }
        else
        {
            item = new PromptItem
            {
                Id = DataService.NewId(),
                Title = BuildTitle(parsed.Model),
                Content = positive,
                Category = "ComfyUI",
                CreatedAt = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
                GenerationInfo = genInfo
            };
            _db.SaveItem(item, true);
            if (images.Count > 0) _db.SaveImages(item.Id, images);
            OnStatus?.Invoke($"已保存: {item.Title}");
        }
        OnSaved?.Invoke(item);
    }

    // ===== 参数提取（移植 comfyui-monitor.js extractParams）=====
    private sealed class WorkflowParams
    {
        public string Positive = "";
        public string Negative = "";
        public string Sampler = "", Scheduler = "", Steps = "", Cfg = "", Denoise = "";
        public string Model = "", Clip = "", Vae = "", Width = "", Height = "", Seed = "";
    }

    private WorkflowParams ExtractParams(JsonElement entry)
    {
        var p = new WorkflowParams();
        var workflow = FindWorkflow(entry);
        if (workflow.ValueKind != JsonValueKind.Object) return p;

        // 第一遍：收集节点
        var textCandidates = new List<(string NodeId, string Field, string Text, string Class)>();
        var samplerNodes = new List<JsonElement>();
        var loaderNodes = new List<JsonElement>();
        var latentNodes = new List<JsonElement>();

        foreach (var nodeProp in workflow.EnumerateObject())
        {
            var node = nodeProp.Value;
            if (node.ValueKind != JsonValueKind.Object) continue;
            if (!node.TryGetProperty("class_type", out var ctEl)) continue;
            var ct = ctEl.GetString() ?? "";
            var inputs = node.TryGetProperty("inputs", out var inp) && inp.ValueKind == JsonValueKind.Object
                ? inp : default;

            var ctLower = ct.ToLowerInvariant();
            if (IsBlacklisted(ctLower))
            {
                // 黑名单节点仍可能包含采样器/加载器角色
            }
            else if (inputs.ValueKind == JsonValueKind.Object)
            {
                foreach (var field in new[] { "text", "prompt", "string", "content", "text_g", "text_l", "text1", "text2", "value", "anything" })
                {
                    if (!inputs.TryGetProperty(field, out var v)) continue;
                    if (v.ValueKind == JsonValueKind.String)
                    {
                        var s = v.GetString()!;
                        if (IsPromptText(s)) textCandidates.Add((nodeProp.Name, field, s, ct));
                    }
                    else if (v.ValueKind == JsonValueKind.Array)
                    {
                        var resolved = ResolveText(workflow, v);
                        if (resolved != null && IsPromptText(resolved))
                            textCandidates.Add((nodeProp.Name, field, resolved, ct));
                    }
                }
            }

            if (ctLower.Contains("sampler")) samplerNodes.Add(node);
            if (ctLower.Contains("loader")) loaderNodes.Add(node);
            if (ctLower.Contains("empty") && ctLower.Contains("latent")) latentNodes.Add(node);
        }

        // 第二遍：采样器参数
        foreach (var node in samplerNodes)
        {
            var inputs = node.TryGetProperty("inputs", out var i2) && i2.ValueKind == JsonValueKind.Object ? i2 : default;
            if (inputs.ValueKind == JsonValueKind.Object)
            {
                ScanParamFields(inputs, p);
                foreach (var refField in new[] { "noise", "sigmas", "sampler", "guider", "latent_image", "cfg", "denoise", "seed", "model" })
                {
                    if (inputs.TryGetProperty(refField, out var rf) && rf.ValueKind == JsonValueKind.Array && rf.GetArrayLength() > 0)
                    {
                        var childId = rf[0].GetString();
                        if (childId != null && workflow.TryGetProperty(childId, out var child) &&
                            child.TryGetProperty("inputs", out var ci) && ci.ValueKind == JsonValueKind.Object)
                        {
                            ScanParamFields(ci, p);
                        }
                    }
                }
            }
        }

        // 兜底参数
        if (string.IsNullOrEmpty(p.Seed) || string.IsNullOrEmpty(p.Steps))
        {
            foreach (var nodeProp in workflow.EnumerateObject())
            {
                var node = nodeProp.Value;
                if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty("inputs", out var in3)) continue;
                if (in3.ValueKind == JsonValueKind.Object) ScanParamFields(in3, p);
            }
        }

        // 第三遍：模型/CLIP/VAE
        foreach (var node in loaderNodes)
        {
            if (!node.TryGetProperty("class_type", out var ctEl)) continue;
            var ct = (ctEl.GetString() ?? "").ToLowerInvariant();
            if (!node.TryGetProperty("inputs", out var inputs) || inputs.ValueKind != JsonValueKind.Object) continue;
            foreach (var kp in inputs.EnumerateObject())
            {
                var val = kp.Value;
                if (val.ValueKind != JsonValueKind.String) continue;
                var s = val.GetString()!;
                if (kp.Name.EndsWith("_name", StringComparison.OrdinalIgnoreCase) || kp.Name == "name")
                {
                    if (System.Text.RegularExpressions.Regex.IsMatch(s, @"\.\w+$"))
                    {
                        if (ct.Contains("unet") || ct.Contains("checkpoint") || ct.Contains("ckpt"))
                        { if (string.IsNullOrEmpty(p.Model)) p.Model = s; }
                        else if (ct.Contains("clip") || ct.Contains("text"))
                        { if (string.IsNullOrEmpty(p.Clip)) p.Clip = s; }
                        else if (ct.Contains("vae"))
                        { if (string.IsNullOrEmpty(p.Vae)) p.Vae = s; }
                        else if (ct.Contains("model") && string.IsNullOrEmpty(p.Model)) p.Model = s;
                    }
                }
            }
        }

        // 第四遍：尺寸
        foreach (var node in latentNodes)
        {
            if (!node.TryGetProperty("inputs", out var inputs) || inputs.ValueKind != JsonValueKind.Object) continue;
            if (string.IsNullOrEmpty(p.Width) && inputs.TryGetProperty("width", out var w)) p.Width = w.ToString();
            if (string.IsNullOrEmpty(p.Height) && inputs.TryGetProperty("height", out var h)) p.Height = h.ToString();
        }
        if (string.IsNullOrEmpty(p.Width) || string.IsNullOrEmpty(p.Height))
        {
            foreach (var nodeProp in workflow.EnumerateObject())
            {
                var node = nodeProp.Value;
                if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty("inputs", out var in4)) continue;
                if (in4.ValueKind != JsonValueKind.Object) continue;
                if (string.IsNullOrEmpty(p.Width) && in4.TryGetProperty("width", out var w)) p.Width = w.ToString();
                if (string.IsNullOrEmpty(p.Height) && in4.TryGetProperty("height", out var h)) p.Height = h.ToString();
            }
        }

        // 第五遍：正/负向提示词（采样器引用链优先，全局最长兜底）
        foreach (var node in samplerNodes)
        {
            if (!node.TryGetProperty("inputs", out var inputs) || inputs.ValueKind != JsonValueKind.Object) continue;
            if (string.IsNullOrEmpty(p.Positive) && inputs.TryGetProperty("positive", out var posRef) && posRef.ValueKind == JsonValueKind.Array)
            {
                var found = FindTextFromRef(workflow, posRef);
                if (found != null) p.Positive = found;
            }
            if (string.IsNullOrEmpty(p.Negative) && inputs.TryGetProperty("negative", out var negRef) && negRef.ValueKind == JsonValueKind.Array)
            {
                var found = FindTextFromRef(workflow, negRef);
                if (found != null) p.Negative = found;
            }
            if (string.IsNullOrEmpty(p.Positive) && inputs.TryGetProperty("guider", out var guiderRef) && guiderRef.ValueKind == JsonValueKind.Array && guiderRef.GetArrayLength() > 0)
            {
                var gid = guiderRef[0].GetString();
                if (gid != null && workflow.TryGetProperty(gid, out var guider) &&
                    guider.TryGetProperty("inputs", out var gi) && gi.ValueKind == JsonValueKind.Object)
                {
                    if (string.IsNullOrEmpty(p.Positive) && gi.TryGetProperty("cond", out var cond) && cond.ValueKind == JsonValueKind.Array)
                    { var f = FindTextFromRef(workflow, cond); if (f != null) p.Positive = f; }
                    if (string.IsNullOrEmpty(p.Negative) && gi.TryGetProperty("uncond", out var uncond) && uncond.ValueKind == JsonValueKind.Array)
                    { var f = FindTextFromRef(workflow, uncond); if (f != null) p.Negative = f; }
                }
            }
        }

        // 全局最长兜底（排除指令文本）
        if (textCandidates.Count > 0)
        {
            var nonInstruction = textCandidates.Where(c => !IsInstructionText(c.Text)).ToList();
            var pool = nonInstruction.Count > 0 ? nonInstruction : textCandidates;
            var longest = pool.OrderByDescending(c => c.Text.Length).First().Text;
            if (string.IsNullOrEmpty(p.Positive))
            {
                p.Positive = longest;
            }
            else if (p.Positive.Length < longest.Length * 0.5)
            {
                p.Positive = longest;
            }
        }

        // 正负向去重
        if (!string.IsNullOrEmpty(p.Positive) && !string.IsNullOrEmpty(p.Negative))
        {
            if (p.Positive == p.Negative || p.Positive.Contains(p.Negative) || p.Negative.Contains(p.Positive))
                p.Negative = "";
        }

        // 指令文本修正
        if (IsInstructionText(p.Positive) && textCandidates.Count > 0)
        {
            var nonInstruction = textCandidates.Where(c => !IsInstructionText(c.Text)).ToList();
            if (nonInstruction.Count > 0) p.Positive = nonInstruction.OrderByDescending(c => c.Text.Length).First().Text;
        }

        return p;
    }

    private static JsonElement FindWorkflow(JsonElement entry)
    {
        if (!entry.TryGetProperty("prompt", out var promptField)) return default;
        if (promptField.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in promptField.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object && ContainsClassType(item)) return item;
            }
        }
        else if (promptField.ValueKind == JsonValueKind.Object)
        {
            if (promptField.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Object)
                return output;
            if (ContainsClassType(promptField)) return promptField;
        }
        return default;
    }

    private static bool ContainsClassType(JsonElement obj)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Object && prop.Value.TryGetProperty("class_type", out _))
                return true;
        }
        return false;
    }

    private static bool IsBlacklisted(string ct) =>
        System.Text.RegularExpressions.Regex.IsMatch(ct,
            "expand|refine|translat|optim|enhance|polish|rewrite|promptassistant|llm|gpt|doubao|deepseek|qwen|glm|chatgpt|tongyi|baidu|ernie");

    private static bool IsPromptText(string s)
    {
        var str = s.Trim();
        if (str.Length < 10) return false;
        if (System.Text.RegularExpressions.Regex.IsMatch(str, @"\.(safetensors|ckpt|pt|bin|gguf|png|jpg|jpeg|webp|json|txt|csv|yaml|yml)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return false;
        if (System.Text.RegularExpressions.Regex.IsMatch(str, @"^[a-z_]+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) && str.Length < 30) return false;
        if (System.Text.RegularExpressions.Regex.IsMatch(str, @"^\d+$")) return false;
        if (System.Text.RegularExpressions.Regex.IsMatch(str, "^(enable|disable|default|auto|none|null|true|false)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return false;
        return str.Contains(' ') || str.Length > 50;
    }

    private static bool IsInstructionText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var lower = text.ToLowerInvariant();
        var patterns = new[]
        {
            "您是", "你是", "你扮演", "你的任务", "you are", "请严格", "请按步骤", "请遵循", "请遵守",
            "请输出", "按要求", "用户输入：", "用户输入:", "输出一段", "扩展后的提示词", "风格规划",
            "不外显", "忠实原意", "t2i 结构", "t2i结构", "不要使用项目符号", "不要使用 markdown",
            "作为提示词工程", "提示词工程专家"
        };
        int count = 0;
        foreach (var p in patterns)
        {
            if (text.Contains(p) || lower.Contains(p)) count++;
        }
        return count >= 2;
    }

    private static void ScanParamFields(JsonElement inputs, WorkflowParams p)
    {
        foreach (var kp in inputs.EnumerateObject())
        {
            var k = kp.Name.ToLowerInvariant();
            var v = kp.Value;
            if (v.ValueKind != JsonValueKind.String && v.ValueKind != JsonValueKind.Number) continue;
            var s = v.ToString();
            if (k == "seed" || k == "noise_seed")
            { if (string.IsNullOrEmpty(p.Seed)) p.Seed = s; }
            else if (k == "steps" || k == "step")
            { if (string.IsNullOrEmpty(p.Steps)) p.Steps = s; }
            else if (k == "cfg" || k == "cfgscale" || k == "cfg_scale")
            { if (string.IsNullOrEmpty(p.Cfg)) p.Cfg = s; }
            else if (k == "sampler_name" || k == "sampler")
            { if (string.IsNullOrEmpty(p.Sampler)) p.Sampler = s; }
            else if (k == "scheduler")
            { if (string.IsNullOrEmpty(p.Scheduler)) p.Scheduler = s; }
            else if (k == "denoise")
            { if (string.IsNullOrEmpty(p.Denoise)) p.Denoise = s; }
            else if (k == "width")
            { if (string.IsNullOrEmpty(p.Width)) p.Width = s; }
            else if (k == "height")
            { if (string.IsNullOrEmpty(p.Height)) p.Height = s; }
        }
    }

    private static string? FindTextFromRef(JsonElement workflow, JsonElement refValue)
    {
        var results = new List<string>();
        CollectTextFromRef(workflow, refValue, new HashSet<string>(), 0, results);
        if (results.Count == 0) return null;
        return results.OrderByDescending(r => r.Length).First();
    }

    private static void CollectTextFromRef(JsonElement workflow, JsonElement refValue, HashSet<string> visited, int depth, List<string> results)
    {
        if (depth > 10) return;
        if (refValue.ValueKind != JsonValueKind.Array || refValue.GetArrayLength() == 0) return;
        var nodeId = refValue[0].GetString();
        if (nodeId == null || !visited.Add(nodeId)) return;
        if (!workflow.TryGetProperty(nodeId, out var node) ||
            !node.TryGetProperty("inputs", out var inputs) || inputs.ValueKind != JsonValueKind.Object) return;

        var ct = node.TryGetProperty("class_type", out var cte) ? (cte.GetString() ?? "") : "";
        if (!IsBlacklisted(ct.ToLowerInvariant()))
        {
            foreach (var field in new[] { "text", "prompt", "string", "content", "text_g", "text_l", "text1", "text2", "value", "anything" })
            {
                if (inputs.TryGetProperty(field, out var fv) && fv.ValueKind == JsonValueKind.String)
                {
                    var s = fv.GetString()!;
                    if (IsPromptText(s)) results.Add(s);
                }
            }
        }
        foreach (var field in new[] { "text1", "text2", "text_g", "text_l", "text", "prompt", "string", "content", "value", "anything", "input", "input1", "input2", "cond", "uncond", "positive", "negative" })
        {
            if (inputs.TryGetProperty(field, out var fv) && fv.ValueKind == JsonValueKind.Array)
                CollectTextFromRef(workflow, fv, visited, depth + 1, results);
        }
        foreach (var kp in inputs.EnumerateObject())
        {
            if (kp.Value.ValueKind == JsonValueKind.Array)
                CollectTextFromRef(workflow, kp.Value, visited, depth + 1, results);
        }
    }

    private static string? ResolveText(JsonElement workflow, JsonElement value, int depth = 0)
    {
        if (depth > 5) return null;
        if (value.ValueKind == JsonValueKind.String) return value.GetString();
        if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() >= 1)
        {
            var nodeId = value[0].GetString();
            if (nodeId == null || !workflow.TryGetProperty(nodeId, out var node) ||
                !node.TryGetProperty("inputs", out var inputs) || inputs.ValueKind != JsonValueKind.Object) return null;
            foreach (var field in new[] { "text", "prompt", "string", "content", "value", "anything", "text1", "text2", "text_g", "text_l" })
            {
                if (inputs.TryGetProperty(field, out var fv))
                {
                    if (fv.ValueKind == JsonValueKind.String) return fv.GetString();
                    if (fv.ValueKind == JsonValueKind.Array) return ResolveText(workflow, fv, depth + 1);
                }
            }
        }
        return null;
    }

    // ===== 图片下载 =====
    private async Task<(List<string> Urls, List<string> Filenames)> DownloadImages(JsonElement entry, CancellationToken ct)
    {
        var result = new List<string>();
        var names = new List<string>();
        if (!entry.TryGetProperty("outputs", out var outputs) || outputs.ValueKind != JsonValueKind.Object) return (result, names);
        foreach (var nodeProp in outputs.EnumerateObject())
        {
            var nodeOut = nodeProp.Value;
            if (nodeOut.ValueKind != JsonValueKind.Object || !nodeOut.TryGetProperty("images", out var images)) continue;
            if (images.ValueKind != JsonValueKind.Array) continue;
            foreach (var img in images.EnumerateArray())
            {
                var type = img.TryGetProperty("type", out var t) ? t.GetString() : "output";
                if (type != "output") continue;
                var filename = img.TryGetProperty("filename", out var f) ? f.GetString() : "";
                if (string.IsNullOrEmpty(filename)) continue;
                var subfolder = img.TryGetProperty("subfolder", out var sf) ? sf.GetString() : "";
                try
                {
                    var url = $"{_baseUrl}/view?filename={Uri.EscapeDataString(filename)}&subfolder={Uri.EscapeDataString(subfolder ?? "")}&type=output";
                    var bytes = await _http.GetByteArrayAsync(url, ct);
                    var mime = filename.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ? "image/webp"
                        : filename.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || filename.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ? "image/jpeg"
                        : "image/png";
                    result.Add($"data:{mime};base64,{Convert.ToBase64String(bytes)}");
                    names.Add(filename);
                }
                catch { }
            }
        }
        return (result, names);
    }

    // ===== generationInfo 构建 =====
    private static object BuildGenerationInfo(string pid, WorkflowParams p, int imageCount, List<string>? imageFiles)
    {
        var size = (!string.IsNullOrEmpty(p.Width) && !string.IsNullOrEmpty(p.Height)) ? $"{p.Width} × {p.Height}" : "";
        var now = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        var gen = new Dictionary<string, object?>
        {
            ["seed"] = p.Seed,
            ["steps"] = p.Steps,
            ["cfgScale"] = p.Cfg,
            ["sampler"] = p.Sampler,
            ["model"] = p.Model,
            ["size"] = size,
            ["negativePrompt"] = p.Negative,
            ["promptId"] = pid,
            ["filenames"] = imageFiles ?? new List<string>(),
            ["createdAt"] = now
        };
        return new Dictionary<string, object?>
        {
            ["model"] = p.Model,
            ["steps"] = p.Steps,
            ["cfgScale"] = p.Cfg,
            ["sampler"] = p.Sampler,
            ["seed"] = p.Seed,
            ["size"] = size,
            ["negativePrompt"] = p.Negative,
            ["source"] = "comfyui",
            ["promptId"] = pid,
            ["clip"] = p.Clip,
            ["vae"] = p.Vae,
            ["scheduler"] = p.Scheduler,
            ["denoise"] = p.Denoise,
            ["imageCount"] = imageCount,
            ["generations"] = new object[] { gen }
        };
    }

    private static object MergeGenerationInfo(object? old, object newGen)
    {
        // 保留旧 generations 历史，追加新记录
        var list = new List<object>();
        if (old is JsonElement je && je.ValueKind == JsonValueKind.Object)
        {
            if (je.TryGetProperty("generations", out var gens) && gens.ValueKind == JsonValueKind.Array)
            {
                foreach (var g in gens.EnumerateArray()) list.Add(CloneElement(g));
            }
        }
        if (newGen is Dictionary<string, object?> nd && nd.TryGetValue("generations", out var ng) && ng is object[] arr && arr.Length > 0)
            list.Add(arr[0]);

        var dict = new Dictionary<string, object?>();
        if (old is JsonElement je2 && je2.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in je2.EnumerateObject())
            {
                if (prop.Name == "generations") continue;
                dict[prop.Name] = CloneElement(prop.Value);
            }
        }
        if (newGen is Dictionary<string, object?> nd2)
        {
            foreach (var kv in nd2) dict[kv.Key] = kv.Value;
        }
        dict["generations"] = list;
        return dict;
    }

    private static object CloneElement(JsonElement el)
    {
        try { return JsonSerializer.Deserialize<object>(el.GetRawText()) ?? ""; }
        catch { return ""; }
    }

    private static string BuildTitle(string model)
    {
        var now = DateTime.Now;
        var ts = $"{now.Year}/{now.Month:00}/{now.Day:00} {now.Hour:00}:{now.Minute:00}:{now.Second:00}";
        var m = string.IsNullOrEmpty(model)
            ? "未知模型"
            : System.Text.RegularExpressions.Regex.Replace(model, @"\.(safetensors|ckpt|pt|bin|gguf)$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return $"ComfyUI · {m} · {ts}";
    }

    private async Task<JsonElement> GetJson(string path)
    {
        var resp = await _http.GetStringAsync(_baseUrl + path).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(resp);
        return doc.RootElement.Clone();
    }

    public void Dispose()
    {
        Stop();
        _http.Dispose();
    }
}
