using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PictureButler;

public partial class MainWindow : Window
{
    // ---- DWM 属性（Windows 11 Mica / 暗色 / 圆角） ----
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMSBT_MAINWINDOW = 2;          // Mica
    private const int DWMWCP_ROUND = 2;               // 圆角

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>本窗口句柄。Shell 文件操作（回收站删除）必须带上它，否则 Shell 会激活别处、把本窗口压到后面。</summary>
    private IntPtr _hwnd;

    private DataService _db = null!;
    private ImageLibraryService _img = null!;
    private LocalHttpServer _http = null!;
    private TrayService? _tray;
    private HotKeyService? _hotkey;
    private ComfyUiMonitor? _comfy;
    private ImgtagRecognizer? _recognizer;
    private string _imgTagDir = "";
    private AppSettings _settings = new();
    private List<PromptItem> _allItems = new();
    private string _searchText = "";
    private int _imgPage = 0;
    private int ImgPageSize => _settings.ImgPageSize;
    private bool _imgLoaded;
    private bool _reallyQuit;
    private Border? _selectedCard;
    private DispatcherTimer? _noticeTimer;
    private DispatcherTimer? _imgCleanTimer;
    // 图片库加载/搜索的取消令牌：一套同时管"搜索防抖"与"过期结果丢弃"
    private CancellationTokenSource? _imgListCts;
    private CancellationTokenSource? _imgSearchCts;
    private CancellationTokenSource? _tagSearchCts;
    private CancellationTokenSource? _promptSearchCts;
    private bool _imgLoading;
    private Action? _noticeClick;
    private int _imgBadgeCount;
    private bool _recognizing;
    private bool _applyingSettings;

    // 图片库模式：all / persons / tags / personPhotos / tagPhotos / idPhotos / docPhotos
    private string _imgMode = "all";
    private long _currentPersonId;
    private long _currentTagId;
    private int _personPage = 0;
    private int _tagPage = 0;
    private string _imgFolderSearch = "";

    // ---- 阶段3：排序 / 状态 / 来源 / 标签 AND 组合（全部下推 SQL） ----
    private string _imgSort = "date_desc";
    private string _imgStatus = "all";
    private string _imgFolder = "";
    private readonly List<long> _imgTagIds = new();
    private readonly List<(long Id, string Name)> _imgTagChips = new();
    private bool _imgFilterReady;       // 初始化完成前忽略筛选事件
    private bool _imgFilterExpanded;    // 0.61.0 批次 2b：筛选行默认收起，用户手动展开
    private List<string> _imgFolderList = new() { "" };  // 来源循环列表（"" = 全部）
    private bool _imgFoldersLoaded;

    // ---- 批量多选状态 ----
    public static readonly DependencyProperty MultiModeVisibleProperty =
        DependencyProperty.Register(nameof(MultiModeVisible), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));
    public bool MultiModeVisible { get => (bool)GetValue(MultiModeVisibleProperty); set => SetValue(MultiModeVisibleProperty, value); }

    public static readonly DependencyProperty PersonMultiVisibleProperty =
        DependencyProperty.Register(nameof(PersonMultiVisible), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));
    public bool PersonMultiVisible { get => (bool)GetValue(PersonMultiVisibleProperty); set => SetValue(PersonMultiVisibleProperty, value); }

    private bool _promptMulti;
    private bool _imgMulti;
    private readonly HashSet<string> _imgSel = new(StringComparer.OrdinalIgnoreCase);
    private bool _personMulti;
    private readonly HashSet<long> _personSel = new();
    private ImgItem? _lastImgFocus; // ItemsControl 无 SelectedItem，键盘操作用最近点击项

    // 调试日志（人物多选问题排查）。
    // 0.61.1：加 [Conditional("DEBUG")] —— Release 编译时编译器自动剥离全部调用点，
    // 不再往程序目录写 debug_person_multi.log（用户要求部署目录不留杂物）。排查时切 Debug 即可恢复。
    private static readonly object _logLock = new();
    [System.Diagnostics.Conditional("DEBUG")]
    private static void LogDebug(string msg)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "debug_person_multi.log");
            lock (_logLock)
            {
                File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}");
            }
        }
        catch { }
    }

    // 提示词关联图片文件名集合（ComfyUI generationInfo.filenames），用于图片库"有提示词"标识
    private HashSet<string> _promptImageFiles = new(StringComparer.OrdinalIgnoreCase);
    private bool _promptFileSetDirty = true;

    private void MarkPromptFileSetDirty() => _promptFileSetDirty = true;

    private void EnsurePromptImageFileSet()
    {
        if (!_promptFileSetDirty) return;
        _promptFileSetDirty = false;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var p in _db.LoadItems())
            {
                if (p.GenerationInfo is not System.Text.Json.JsonElement je || je.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                CollectFilenames(je, set);
                if (je.TryGetProperty("generations", out var gens) && gens.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var g in gens.EnumerateArray())
                        if (g.ValueKind == System.Text.Json.JsonValueKind.Object) CollectFilenames(g, set);
                }
            }
        }
        catch { }
        _promptImageFiles = set;
    }

    private static void CollectFilenames(System.Text.Json.JsonElement obj, HashSet<string> set)
    {
        if (obj.TryGetProperty("filenames", out var fn) && fn.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var f in fn.EnumerateArray())
            {
                var name = f.GetString();
                if (!string.IsNullOrEmpty(name)) set.Add(name);
            }
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        LogDebug("=== MainWindow ctor ===");
        // 提示词搜索：250ms 防抖，避免每次按键都全量过滤
        SearchBox.TextChanged += (_, _) =>
        {
            _searchText = SearchBox.Text.Trim().ToLowerInvariant();
            SchedulePromptFilter();
        };
        // 图片库搜索：300ms 防抖；Enter 立即查询
        ImgSearchBox.TextChanged += (_, _) => ScheduleImgSearch();
        ImgSearchBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                _imgSearchCts?.Cancel();
                _imgPage = 0;
                _ = LoadImgPageAsync();
            }
        };
        // ===== 图片库鼠标交互兜底 + 诊断（0.31）=====
        // 背景：用户真机滚轮/点击/多选全部失效，而程序化(UIA)操作正常。
        // 已排除：覆盖层(UIA 无)、DPI(单屏 PerMonitorV2)、视图重叠(切换逻辑正确)。
        // 措施：①窗口级滚轮兜底（鼠标在图片库区域内即滚动 ImgListScroll，绕开事件路由）；
        //       ②窗口级 WPF 命中测试诊断（记录鼠标位置真正命中的视觉元素链，一次定案）；
        //       ③窗口级左键兜底（命中到卡片 Border 时直接打开，绕开卡片事件被拦截的可能）。
        PreviewMouseWheel += (_, e) =>
        {
            try
            {
                var pt = e.GetPosition(this);
                Diag2($"MWHEEL delta={e.Delta} at=({pt.X:F0},{pt.Y:F0}) hit={HitChainOf(this, pt)}");
                if (ImgView.Visibility == Visibility.Visible && ImgListScroll.ScrollableHeight > 0)
                {
                    // 鼠标在图片库内容区域内才接管滚轮（避免影响搜索框/下拉等）
                    var sv = ImgListScroll;
                    var r = new System.Windows.Rect(sv.TransformToAncestor(this).Transform(new System.Windows.Point(0, 0)),
                                                   new System.Windows.Size(sv.ActualWidth, sv.ActualHeight));
                    if (r.Contains(pt))
                    {
                        double steps = e.Delta / 120.0;
                        double next = Math.Clamp(sv.VerticalOffset - steps * 56, 0, sv.ScrollableHeight);
                        sv.ScrollToVerticalOffset(next);
                        e.Handled = true;
                    }
                }
            }
            catch { }
        };
        PreviewMouseDown += (_, e) =>
        {
            try
            {
                var pt = e.GetPosition(this);
                Diag2($"MDOWN at=({pt.X:F0},{pt.Y:F0}) hit={HitChainOf(this, pt)} input={InputHitOf(this, pt)}");
            }
            catch { }
        };
        PreviewMouseLeftButtonUp += async (_, e) =>
        {
            try
            {
                if (ImgView.Visibility != Visibility.Visible) return;
                // 人物模式兜底：点击人物卡片 → 进入该人物照片（绕过卡片事件被拦截的情况）
                // 多选模式下不走兜底，由卡片覆盖层处理勾选
                if (PersonGrid.Visibility == Visibility.Visible && !_personMulti)
                {
                    // 与图片网格同理：必须先限定在滚动可视区内。
                    // PersonGrid 也随内容滚动，人物列表滚动过后，点击上方页签（全部/标签）的坐标
                    // 换算进去会落在某个人物卡片上，被误当成点人物并 return，页签点击因此丢失。
                    var ppt = e.GetPosition(ImgListScroll);
                    var personInView = ppt.X >= 0 && ppt.Y >= 0
                                       && ppt.X <= ImgListScroll.ActualWidth
                                       && ppt.Y <= ImgListScroll.ActualHeight;
                    if (personInView)
                    {
                        var person = ItemAtPoint<PersonItem>(PersonGrid, e.GetPosition(PersonGrid));
                        if (person != null && !e.Handled)
                        {
                            Diag2($"PERSON-FALLBACK '{person.Name}' id={person.Id}");
                            _imgMode = "personPhotos";
                            _currentPersonId = person.Id;
                            _personPage = 0;
                            ImgBackBtn.Visibility = Visibility.Visible;
                            ShowPersonActionBar(person);
                            _ = LoadImgPageAsync();
                            e.Handled = true;
                            return;
                        }
                    }
                    else
                    {
                        Diag2($"FB-person skip: outside viewport pt=({ppt.X:F0},{ppt.Y:F0})");
                    }
                }
                // 图片网格模式
                if (ImgGrid.Visibility != Visibility.Visible) return;
                var pt = e.GetPosition(ImgListScroll);
                // ★ 必须先限定在滚动可视区内。
                //   原因：ImgGrid 位于滚动容器内部、会随内容滚动移动，只用相对 ImgGrid 的坐标判断时，
                //   网格一旦向下滚过，点击网格上方（页签、返回、多选等按钮）的换算坐标会正好落在某张
                //   卡片上，于是被当成「点了图片」打开——即用户看到的「按钮穿透」。
                if (pt.X < 0 || pt.Y < 0 || pt.X > ImgListScroll.ActualWidth || pt.Y > ImgListScroll.ActualHeight)
                {
                    Diag2($"FB skip: outside viewport pt=({pt.X:F0},{pt.Y:F0})");
                    return;
                }
                var item2 = ItemAtPoint<ImgItem>(ImgGrid, e.GetPosition(ImgGrid));
                Diag2($"FB mode={_imgMode} status={_imgStatus} pt=({pt.X:F0},{pt.Y:F0}) handled={e.Handled} " +
                      $"imgVis={ImgGrid.Visibility} personVis={PersonGrid.Visibility} maskVis={ImgLoadingMask?.Visibility} found={(item2 != null)}");
                if (item2 != null && !e.Handled)
                {
                    if (_imgMulti)
                    {
                        // 多选兜底：点击卡片切换选中（等效勾选框；勾选框被遮挡时也能工作）
                        item2.Selected = !item2.Selected;
                        if (item2.Selected) _imgSel.Add(item2.Path); else _imgSel.Remove(item2.Path);
                        UpdateImgSelCount();
                        Diag2($"CHK-FALLBACK sel={item2.Selected} path='{item2.Path}'");
                        e.Handled = true;
                        return;
                    }
                    Diag2($"CLICK-FALLBACK open='{item2.Path}'");
                    await OpenImgViewerAsync(item2);
                    e.Handled = true;
                }
            }
            catch { }
        };
        // 右键菜单兜底（0.46.3）：图片/人物卡片自身的鼠标事件收不到（历史遗留：图片打开全靠上面的
        // 窗口级左键兜底），因此 WPF 的 ContextMenuService 也找不到卡片上的 ContextMenu，
        // 表现为「图片库右键无任何反应」。这里按同一套坐标判定解析出卡片，直接打开它自己的菜单。
        PreviewMouseRightButtonDown += (_, e) =>
        {
            try
            {
                if (ImgView.Visibility != Visibility.Visible) return;
                var pt = e.GetPosition(ImgListScroll);
                if (pt.X < 0 || pt.Y < 0 || pt.X > ImgListScroll.ActualWidth || pt.Y > ImgListScroll.ActualHeight) return;

                // 若默认命中链上已存在 ContextMenu，交给 WPF 自己开，避免重复弹两次
                var hasMenu = HitHasContextMenu(this, e.GetPosition(this));

                if (PersonGrid.Visibility == Visibility.Visible)
                {
                    var person = ItemAtPoint<PersonItem>(PersonGrid, e.GetPosition(PersonGrid));
                    Diag2($"RBTN person found={person != null} hasMenu={hasMenu} input={InputHitOf(this, e.GetPosition(this))}");
                    if (person != null && !hasMenu && OpenCardContextMenu(PersonGrid, person)) e.Handled = true;
                    return;
                }
                if (ImgGrid.Visibility != Visibility.Visible) return;
                var item = ItemAtPoint<ImgItem>(ImgGrid, e.GetPosition(ImgGrid));
                Diag2($"RBTN img found={item != null} hasMenu={hasMenu} input={InputHitOf(this, e.GetPosition(this))}");
                if (item != null && !hasMenu && OpenCardContextMenu(ImgGrid, item)) e.Handled = true;
            }
            catch (Exception ex) { Diag2("RBTN err " + ex.Message); }
        };
        // 保留：网格内滚轮到达记录（对比窗口级 MWHEEL，判断事件是否路由进网格）
        if (ImgListScroll != null)
        {
            ImgListScroll.Background = System.Windows.Media.Brushes.Transparent;
            ImgListScroll.PreviewMouseWheel += (_, e) =>
            {
                try
                {
                    Diag2($"WHEEL-in-SV delta={e.Delta} sv.sh={ImgListScroll.ScrollableHeight:F0} off={ImgListScroll.VerticalOffset:F0}");
                }
                catch { }
            };
        }
        // 标签搜索：200ms 防抖，Enter 立即
        TagSearchBox.TextChanged += (_, _) => ScheduleTagSearch();
        TagSearchBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { _tagSearchCts?.Cancel(); _ = LoadImgPageAsync(); }
        };

        // 图片库幽灵卡片清理：外部（文件夹）删除文件后，定时把占位卡片和索引清掉
        _imgCleanTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _imgCleanTimer.Tick += (_, _) => CleanupGhostCards();
        _imgCleanTimer.Start();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        // 从文件夹/外部删完切回程序时，立即清理占位
        if (ImgGrid.IsVisible) CleanupGhostCards();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        _hwnd = hwnd;
        int dark = 1;
        int mica = DWMSBT_MAINWINDOW;
        int corner = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref mica, sizeof(int));
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

        // 版本号标注（纯数字，不带中文；用户 2026-09-13 明确取消中文版本号）
        try
        {
            var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            if (ver != null)
            {
                var v = $"{ver.Major}.{ver.Minor}.{ver.Build}";
                VersionText.Text = $"PictureButler v{v}";
                // 设置页「关于」也显示版本号（用户 2026-09-14 约定：版本号在设置页显示）
                SettingsVersionText.Text = $"版本 {v}";
                // 底部常驻状态条右侧副显示（0.61.0 批次 2b）
                StatusVersionText.Text = $"v{v}";
            }
        }
        catch { }

        // 加载数据（后台线程避免阻塞 UI）
        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "com.picturebutler.app", "prompts.db");
        _db = new DataService(dbPath);

        // 启动本地 HTTP 服务（供浏览器扩展拉取星标/提示词）
        try
        {
            _http = new LocalHttpServer();
            _http.Start(_db, () => _allItems);
            HttpStatusText.Text = $"本地服务已启动 · 127.0.0.1:{_http.Port}";
            TintStatus(HttpStatusText, true);
        }
        catch (Exception ex)
        {
            HttpStatusText.Text = "本地服务启动失败: " + ex.Message;
            TintStatus(HttpStatusText, false);
        }

        // 托盘常驻 + 全局热键（HTTP 服务保持运行，扩展随时可连接）
        _tray = new TrayService(ShowMainWindow, QuitApp);
        _tray.Init();
        try
        {
            _hotkey = new HotKeyService(ShowMainWindow);
            _hotkey.Register(this);
        }
        catch { }

        // imgtag 识别服务（人脸识别 / AI 打标）——已并入程序包：程序目录 imgtag\ 优先
        // 关键：必须先从磁盘加载设置，否则此处用字段默认的空设置 Save 会把已保存的图片来源夹等清空
        _settings = AppSettings.Load();
        var dbPathBefore = _settings.ImgtagDbPath;
        _settings.ImgtagDbPath = AppSettings.ResolveImgTagDb(_settings.ImgtagDbPath);
        // 启动时以设置为准同步注册表自启项，清理「设置关但 Run 仍残留」导致的开机自启
        AppSettings.SyncAutoStartWithSettings(_settings.AutoStart);
        // 仅路径被解析变更时才写盘；避免无意义 Save 把配置写坏
        if (!string.Equals(dbPathBefore, _settings.ImgtagDbPath, StringComparison.OrdinalIgnoreCase))
            _settings.Save();
        // 来源列表空了就从已入库目录恢复（只恢复配置，不动任何图片/识别数据）
        RecoverImageFoldersIfEmpty();
        var imgTagDir = AppSettings.ResolveImgTagDir(_settings.ImgtagDbPath);
        _imgTagDir = imgTagDir;
        _recognizer = new ImgtagRecognizer(
            imgTagDir,
            _settings.ImgtagDbPath,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "com.picturebutler.app", "image_index.db"));

        // 把设置中的人脸识别参数同步到引擎
        _recognizer.FaceOptions.Sensitivity = _settings.FaceDetectSensitivity;
        _recognizer.FaceOptions.MinFaceSize = _settings.FaceMinSize;
        _recognizer.FaceOptions.ClusterSensitivity = _settings.FaceClusterSensitivity;
        // 同步写入识别库 settings 表（Rust 侧读取），保证界面显示与引擎实际使用的值一致
        try
        {
            var okWrite = _recognizer.WriteFaceSettingsToDb();
            LogDebug($"启动同步人脸参数 → 识别库: ok={okWrite} det={_settings.FaceDetectSensitivity} min={_settings.FaceMinSize} cluster={_settings.FaceClusterSensitivity}");
        }
        catch (Exception ex) { LogDebug("启动同步人脸参数异常: " + ex.Message); }

        // 人脸缩略图：改为**进程内**生成（imgtag_native），经程序自身 HTTP 服务对外提供；
        // 不再依赖独立 imgtag 服务与 8520 端口。
        var recognizerForThumb = _recognizer;
        LocalHttpServer.FaceThumbnailProvider = fid =>
        {
            try
            {
                if (!recognizerForThumb.ServerRunning()) recognizerForThumb.EnsureServer(out _);
                var p = recognizerForThumb.EnsureFaceThumbnail(fid);
                if (string.IsNullOrEmpty(p) || !File.Exists(p)) return null;
                return File.ReadAllBytes(p);
            }
            catch { return null; }
        };

        _ = LoadDataAsync();
        ApplySettingsToUi();
        // 后台给历史空名人物补名「人物N」（人脸识别生成的旧人物不再是无名；不阻塞启动）
        try
        {
            var recognizerForNaming = _recognizer;
            _ = Task.Run(() => { try { recognizerForNaming?.NameUnnamedPersons(); } catch { } });
        }
        catch { }
        // 识别引擎改为**用到时才初始化**（识别人脸 / AI 打标 / 人脸缩略图）。
        // 启动路径不扫库、不清库、不预加载模型，保证点开软件秒进界面。
    }

    private async Task LoadDataAsync()
    {
        var items = await Task.Run(() => _db.LoadItems());
        _allItems = items;
        ApplyFilter();
        CountText.Text = $"共 {items.Count} 条";
        MarkPromptFileSetDirty();
        // 后台批量加载卡片预览图（不阻塞 UI，文字先行、图片陆续填充）
        _ = LoadPreviewImagesAsync(items);
    }

    /// <summary>后台线程逐条解码预览图（缩略尺寸，控制内存），经 Dispatcher 批量回填</summary>
    private async Task LoadPreviewImagesAsync(List<PromptItem> items)
    {
        var pending = items.Where(i => i.PreviewImage == null && i.HasPreviewImage == true).ToList();
        if (pending.Count == 0) return;
        const int batch = 8;
        var buffer = new List<(PromptItem Item, System.Windows.Media.ImageSource? Img)>(batch);
        await Task.Run(() =>
        {
            foreach (var it in pending)
            {
                try
                {
                    var dataUrl = _db.LoadPreviewImage(it.Id);
                    if (string.IsNullOrEmpty(dataUrl) || !dataUrl.StartsWith("data:image"))
                    {
                        buffer.Add((it, null));
                    }
                    else
                    {
                        var bmp = ThumbDataUrlToBitmap(dataUrl);
                        bmp?.Freeze();
                        buffer.Add((it, bmp));
                    }
                }
                catch
                {
                    buffer.Add((it, null));
                }
                if (buffer.Count >= batch)
                {
                    FlushBuffer(buffer);
                    buffer.Clear();
                }
            }
            FlushBuffer(buffer);
        });
    }

    /// <summary>缩略图解码：GDI+ 缩放（WPF DecodePixelWidth 对 JPEG 不生效，会全尺寸解码爆内存）</summary>
    private static System.Windows.Media.Imaging.BitmapImage? ThumbDataUrlToBitmap(string dataUrl)
    {
        try
        {
            var comma = dataUrl.IndexOf(',');
            if (comma < 0) return null;
            var bytes = Convert.FromBase64String(dataUrl[(comma + 1)..]);
            using var ms = new MemoryStream(bytes);
            using var src = System.Drawing.Image.FromStream(ms);
            const int targetW = 240;
            int targetH = Math.Max(1, (int)Math.Round(src.Height * (double)targetW / src.Width));
            using var small = new System.Drawing.Bitmap(targetW, targetH);
            using (var g = System.Drawing.Graphics.FromImage(small))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, 0, 0, targetW, targetH);
            }
            using var png = new MemoryStream();
            small.Save(png, System.Drawing.Imaging.ImageFormat.Png);
            png.Position = 0;
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.StreamSource = png;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private void FlushBuffer(List<(PromptItem Item, System.Windows.Media.ImageSource? Img)> buffer)
    {
        if (buffer.Count == 0) return;
        // 跨线程设置绑定属性必须在 UI 线程
        Dispatcher.Invoke(() =>
        {
            foreach (var (item, img) in buffer)
            {
                item.PreviewImage = img;
            }
        });
    }

    // ==================== 设置 ====================

    /// <summary>来源列表为空时从已入库目录恢复（只恢复配置，不删任何数据）。</summary>
    private void RecoverImageFoldersIfEmpty()
    {
        if (_settings == null || _settings.ImageFolders.Count > 0) return;
        try
        {
            var idx = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "com.picturebutler.app", "image_index.db");
            if (!File.Exists(idx)) return;
            var known = new ImageLibraryService(idx, _settings.ImgtagDbPath).DistinctSelfFolders();
            if (known.Count > 0)
            {
                _settings.ImageFolders.AddRange(known);
                _settings.Save();
            }
        }
        catch { }
    }

    private void ApplySettingsToUi()
    {
        _settings = AppSettings.Load();
        RecoverImageFoldersIfEmpty();
        // 加载/回填 UI 期间屏蔽控件事件，避免 IsChecked 赋值再触发 AutoStart_Changed 误写注册表
        _applyingSettings = true;
        try
        {
        AutoStartChk.IsChecked = _settings.AutoStart;
        switch (_settings.CardSize)
        {
            case "compact": CardCompact.IsChecked = true; break;
            case "roomy": CardRoomy.IsChecked = true; break;
            default: CardStandard.IsChecked = true; break;
        }
        if (ThemeManager.IsDark(_settings.Theme)) { if (ThemeDark != null) ThemeDark.IsChecked = true; }
        else if (ThemeLight != null) ThemeLight.IsChecked = true;
        ImgDbPathBox.Text = _settings.ImgtagDbPath;
        FolderList.ItemsSource = null;
        FolderList.ItemsSource = _settings.ImageFolders.ToList();
        ScanStatus.Text = _settings.ImageFolders.Count > 0 ? $"已配置 {_settings.ImageFolders.Count} 个文件夹" : "未配置文件夹";
        if (_settings.ImgPageSize <= 30) Page30.IsChecked = true;
        else if (_settings.ImgPageSize >= 90) Page90.IsChecked = true;
        else Page60.IsChecked = true;
        ApplyPromptViewSetting();
        ComfyMonitorChk.IsChecked = _settings.ComfyMonitorEnabled;
        ComfyUrlBox.Text = _settings.ComfyUrl;
        ComfyStatus.Text = _settings.ComfyMonitorEnabled ? "监听中" : "已停止";
        TintStatus(ComfyStatus, _settings.ComfyMonitorEnabled ? true : (bool?)null);
        if (_settings.ComfyMonitorEnabled) StartComfyMonitor();

        // 人脸识别设置
        try
        {
            switch (_settings.FaceDetectSensitivity)
            {
                case "low": if (FaceSensLow != null) FaceSensLow.IsChecked = true; break;
                case "high": if (FaceSensHigh != null) FaceSensHigh.IsChecked = true; break;
                default: if (FaceSensStandard != null) FaceSensStandard.IsChecked = true; break;
            }
            if (FaceMinSizeSlider != null) FaceMinSizeSlider.Value = _settings.FaceMinSize;
            if (FaceMinSizeText != null) FaceMinSizeText.Text = _settings.FaceMinSize + " px";
            if (FaceAutoRecogChk != null) FaceAutoRecogChk.IsChecked = _settings.FaceAutoRecognize;
            switch (_settings.FaceClusterSensitivity)
            {
                case "conservative": if (FaceClusterConservative != null) FaceClusterConservative.IsChecked = true; break;
                case "aggressive": if (FaceClusterAggressive != null) FaceClusterAggressive.IsChecked = true; break;
                default: if (FaceClusterStandard != null) FaceClusterStandard.IsChecked = true; break;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("人脸识别设置应用失败: " + ex.Message);
        }
        // 同步到识别引擎
        if (_recognizer != null)
        {
            _recognizer.FaceOptions.Sensitivity = _settings.FaceDetectSensitivity;
            _recognizer.FaceOptions.MinFaceSize = _settings.FaceMinSize;
            _recognizer.FaceOptions.ClusterSensitivity = _settings.FaceClusterSensitivity;
        }
        DataInfoText.Text =
            "提示词库：%APPDATA%\\com.picturebutler.app\\prompts.db\n" +
            "图片文件夹：" + (_settings.ImageFolders.Count > 0 ? string.Join("；", _settings.ImageFolders) : "未配置") + "\n" +
            "识别库：" + _settings.ImgtagDbPath + "\n" +
            "本地服务：" + (_http?.Port > 0 ? $"127.0.0.1:{_http.Port}" : "未启动");

        // 关键：显式应用卡片模板并设置行高（CardSize_Changed 的防重入守卫会拦截同名设置）
        PromptGrid.ItemTemplate = (DataTemplate)FindResource("PromptCardTemplate");
        CardPreviewMaxHeight = _settings.CardSize switch { "compact" => 19.0, "roomy" => 57.0, _ => 38.0 };
        }
        finally
        {
            _applyingSettings = false;
        }
        // UI 回填完成后再次以设置为准同步注册表（防御启动路径上的任何残留）
        AppSettings.SyncAutoStartWithSettings(_settings.AutoStart);
    }

    private void AutoStart_Changed(object sender, RoutedEventArgs e)
    {
        if (_applyingSettings || AutoStartChk == null) return;
        _settings.AutoStart = AutoStartChk.IsChecked == true;
        _settings.Save();
        AppSettings.SetAutoStart(_settings.AutoStart);
    }

    // ===== ComfyUI 监听 =====
    private void ComfyMonitor_Changed(object sender, RoutedEventArgs e)
    {
        if (ComfyMonitorChk == null) return;
        _settings.ComfyMonitorEnabled = ComfyMonitorChk.IsChecked == true;
        _settings.Save();
        if (_settings.ComfyMonitorEnabled) StartComfyMonitor();
        else StopComfyMonitor();
    }

    private void ComfyUrl_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (ComfyUrlBox == null) return;
        _settings.ComfyUrl = ComfyUrlBox.Text.Trim();
        _settings.Save();
        // 已开启监听时地址变化立即重启监听
        if (_settings.ComfyMonitorEnabled && _comfy != null && _comfy.Running)
        {
            StartComfyMonitor();
            ComfyStatus.Text = "地址已更新，监听重启中…";
        }
    }

    private async void TestComfy_Click(object sender, RoutedEventArgs e)
    {
        ComfyStatus.Text = "检测中…";
        TintStatus(ComfyStatus, null);
        var url = (ComfyUrlBox.Text.Trim().TrimEnd('/'));
        if (string.IsNullOrEmpty(url)) url = "http://127.0.0.1:8188";
        try
        {
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            var json = await client.GetStringAsync(url + "/history?max_items=1");
            ComfyStatus.Text = json.Length > 5 ? "连接成功（ComfyUI 正在运行）" : "连接成功";
            TintStatus(ComfyStatus, true);
        }
        catch (Exception ex)
        {
            ComfyStatus.Text = "无法连接：" + ex.Message;
            TintStatus(ComfyStatus, false);
        }
    }

    // ===== 人脸识别设置 =====

    private void FaceSensitivity_Changed(object sender, RoutedEventArgs e)
    {
        if (FaceSensLow == null || FaceSensHigh == null || FaceSensStandard == null) return;
        string val = FaceSensLow.IsChecked == true ? "low"
                   : FaceSensHigh.IsChecked == true ? "high"
                   : "standard";
        _settings.FaceDetectSensitivity = val;
        _settings.Save();
        if (_recognizer != null)
        {
            _recognizer.FaceOptions.Sensitivity = val;
            _ = ApplyFaceSettingAsync();
        }
    }

    /// <summary>人脸参数变更后的防抖应用：写入识别库并重启引擎。
    /// 拖动滑块会连续触发 ValueChanged，防抖可避免反复重启引擎。</summary>
    private System.Threading.CancellationTokenSource? _faceSettingCts;
    private async Task ApplyFaceSettingAsync()
    {
        try
        {
            _faceSettingCts?.Cancel();
            _faceSettingCts = new System.Threading.CancellationTokenSource();
            var token = _faceSettingCts.Token;
            await Task.Delay(700, token);
            if (_recognizer == null) return;
            if (_recognizing || _imgBatchRunning) { ShowNotice("参数已保存，将在下次识别时生效"); return; }
            var ok = await Task.Run(() => _recognizer!.ApplyFaceSettings(out _));
            ShowNotice(ok ? "人脸参数已保存并生效" : "参数已保存，重启程序后生效");
        }
        catch (TaskCanceledException) { }
        catch { }
    }

    private void FaceMinSize_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FaceMinSizeSlider == null || FaceMinSizeText == null) return;
        int v = (int)FaceMinSizeSlider.Value;
        FaceMinSizeText.Text = v + " px";
        _settings.FaceMinSize = v;
        _settings.Save();
        if (_recognizer != null)
        {
            _recognizer.FaceOptions.MinFaceSize = v;
            _ = ApplyFaceSettingAsync();
        }
    }

    private void FaceAutoRecognize_Changed(object sender, RoutedEventArgs e)
    {
        if (FaceAutoRecogChk == null) return;
        _settings.FaceAutoRecognize = FaceAutoRecogChk.IsChecked == true;
        _settings.Save();
    }

    private void FaceClusterSensitivity_Changed(object sender, RoutedEventArgs e)
    {
        if (FaceClusterConservative == null || FaceClusterAggressive == null || FaceClusterStandard == null) return;
        string val = FaceClusterConservative.IsChecked == true ? "conservative"
                   : FaceClusterAggressive.IsChecked == true ? "aggressive"
                   : "standard";
        _settings.FaceClusterSensitivity = val;
        _settings.Save();
        if (_recognizer != null)
        {
            _recognizer.FaceOptions.ClusterSensitivity = val;
            _ = ApplyFaceSettingAsync();
        }
    }

    private void ClearFaceThumbnails_Click(object sender, RoutedEventArgs e)
    {
        if (_recognizer == null) return;
        var (count, bytes) = _recognizer.FaceThumbnailStats();
        if (count == 0)
        {
            FaceThumbStats.Text = "缓存已是空的";
            return;
        }
        var mb = bytes / 1024.0 / 1024.0;
        var ok = MessageBox.Show(this,
            $"人脸缩略图缓存共 {count} 个文件、约 {mb:F1} MB，是否全部清空？\n清空后下次查看人物/人脸时会重新生成。",
            "清理人脸缩略图缓存", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ok != MessageBoxResult.Yes) return;
        var removed = _recognizer.ClearFaceThumbnails();
        UpdateFaceThumbStats();
        ShowNotice($"已清理 {removed} 个人脸缩略图");
    }

    private void UpdateFaceThumbStats()
    {
        if (_recognizer == null || FaceThumbStats == null) return;
        var (count, bytes) = _recognizer.FaceThumbnailStats();
        var mb = bytes / 1024.0 / 1024.0;
        FaceThumbStats.Text = $"{count} 张人脸 · {mb:F1} MB";
    }

    private void FaceScanUnscanned_Click(object sender, RoutedEventArgs e)
    {
        if (_recognizing) return;
        ImgRecognize_Click(sender, e);   // 复用顶部「识别图片（人脸）」的增量扫描逻辑
    }

    private async void FaceRescanAll_Click(object sender, RoutedEventArgs e)
    {
        if (_recognizing) return;
        var ok = MessageBox.Show(this,
            "将对全部图片强制重新识别人脸（覆盖已有结果）。是否继续？",
            "强制重新扫描", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ok != MessageBoxResult.Yes) return;

        _recognizing = true;
        FaceScanStats.Text = "准备中…";
        try
        {
            string syncMsg = "";
            await Task.Run(() => _recognizer.SyncNewImages(out syncMsg));
            var progress = new Action<string>(m => Dispatcher.Invoke(() => FaceScanStats.Text = m));
            var result = await Task.Run(() => _recognizer.ScanFacesAsync(null, progress, force: true));
            if (!result.Ok)
            {
                FaceScanStats.Text = "识别失败：" + result.Error;
                return;
            }
            if (_img != null) await Task.Run(() => _img.ReassignOrphanFaces());
            FaceScanStats.Text = $"已识别 {result.Scanned} / 全部 · 人脸 {result.Faces} 个";
            ShowNotice($"重新扫描完成：{result.Faces} 张人脸，人物已更新");
            await RefreshImgLibraryAsync();
            UpdateFaceScanStats();
            UpdateFaceThumbStats();
        }
        catch (Exception ex)
        {
            FaceScanStats.Text = "识别出错：" + ex.Message;
        }
        finally
        {
            _recognizing = false;
        }
    }

    private void UpdateFaceScanStats()
    {
        if (_img == null || FaceScanStats == null) return;
        try
        {
            var stats = _img.GetFaceScanStats();
            FaceScanStats.Text = $"已识别 {stats.Scanned} / {stats.Total} 张 · 人脸 {stats.Faces} 个";
        }
        catch { }
    }

    private void StartComfyMonitor()
    {
        try
        {
            StopComfyMonitor();
            _comfy = new ComfyUiMonitor(_db)
            {
                OnStatus = msg => Dispatcher.Invoke(() => { if (ComfyStatus != null) ComfyStatus.Text = msg; }),
                OnSaved = item => Dispatcher.Invoke(async () =>
                {
                    // 刷新提示词列表
                    _ = LoadDataAsync();
                    // 视觉提醒：托盘气泡 + 主窗口提示条（感知 ComfyUI 生成结果）
                    try
                    {
                        var title = item.Title ?? "ComfyUI 生成完成";
                        var hasImg = item.HasGalleryImages == true ? "（多图）" : item.HasPreviewImage == true ? "（含图）" : "";
                        var shortTitle = title.Length > 26 ? title[..26] + "…" : title;
                        _tray?.Notify("PictureButler · ComfyUI", shortTitle + " 已保存" + hasImg);
                        ShowNotice(shortTitle + " 已保存" + hasImg);
                    }
                    catch { }
                    // ComfyUI 输出目录自动加入图片库来源并扫描，让生成图片出现在图片库
                    try
                    {
                        var outDir = ComfyUiMonitor.DetectOutputDir();
                        if (outDir != null)
                        {
                            bool changed = !_settings.ImageFolders.Any(f =>
                                string.Equals(f.TrimEnd('\\'), outDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
                            if (changed)
                            {
                                _settings.ImageFolders.Add(outDir);
                                _settings.Save();
                                SyncImgAllowedRoots();
                                FolderList.ItemsSource = null;
                                FolderList.ItemsSource = _settings.ImageFolders.ToList();
                                DataInfoText.Text =
                                    "提示词库：%APPDATA%\\com.picturebutler.app\\prompts.db\n" +
                                    "图片文件夹：" + (_settings.ImageFolders.Count > 0 ? string.Join("；", _settings.ImageFolders) : "未配置") + "\n" +
                                    "识别库：" + _settings.ImgtagDbPath + "\n" +
                                    "本地服务：" + (_http?.Port > 0 ? $"127.0.0.1:{_http.Port}" : "未启动");
                            }
                            // 扫描图片库（增量），让新生成图入库
                            if (_img == null) RebuildImgService();
                            var added = await Task.Run(() => _img.ScanFoldersAsync(new List<string> { outDir }, null));
                            if (added > 0)
                            {
                                ScanStatus.Text = $"扫描 ComfyUI 输出：新增 {added} 张";
                                // 强视觉提醒：侧栏未读红点 + 可点击横幅（点击跳到图片库·全部）
                                AddImagesBadge(added);
                                ShowNotice($"ComfyUI 新增 {added} 张图片，点击查看", () =>
                                {
                                    NavImages.IsChecked = true;
                                    if (ImgTabAll.IsChecked == true) { _imgPage = 0; _ = LoadImgPageAsync(); }
                                    else ImgTabAll.IsChecked = true;
                                });
                                if (_imgMode == "all") await LoadImgPageAsync();

                                // 自动识别人脸（如果开启）
                                if (_settings.FaceAutoRecognize && _recognizer != null)
                                {
                                    _ = Task.Run(async () =>
                                    {
                                        try
                                        {
                                            if (_recognizer.EnsureServer(out _))
                                            {
                                                // 同步新图到识别库并增量扫描
                                                _recognizer.SyncNewImages(out _);
                                                var r = await _recognizer.ScanFacesAsync(null, null);
                                                if (r.Faces > 0)
                                                {
                                                    Dispatcher.Invoke(() =>
                                                    {
                                                        ShowNotice($"自动识别完成：检出 {r.Faces} 张人脸");
                                                        UpdateFaceScanStats();
                                                        UpdateFaceThumbStats();
                                                    });
                                                }
                                            }
                                        }
                                        catch { }
                                    });
                                }
                            }
                        }
                    }
                    catch { }
                })
            };
            _comfy.Start(_settings.ComfyUrl);
        }
        catch (Exception ex)
        {
            ComfyStatus.Text = "监听启动失败：" + ex.Message;
            TintStatus(ComfyStatus, false);
        }
    }

    private void StopComfyMonitor()
    {
        try
        {
            _comfy?.Stop();
            _comfy = null;
            if (ComfyStatus != null && !_settings.ComfyMonitorEnabled) ComfyStatus.Text = "已停止";
        }
        catch { }
    }

    /// <summary>主窗口顶部提示条（橙色横幅，5 秒后自动消失）。传入 onClick 时整条可点击。</summary>
    private void ShowNotice(string text, Action? onClick = null)
    {
        try
        {
            if (NoticeBar == null) return;
            NoticeBarText.Text = text;
            NoticeBar.Visibility = Visibility.Visible;
            FadeIn(NoticeBar, 180);          // 进入 180ms（只动 opacity，不抢焦点）
            _noticeClick = onClick;
            // 有点击动作时才允许命中并显示手型
            NoticeBar.IsHitTestVisible = onClick != null;
            NoticeBar.Cursor = onClick != null ? AppCursors.Hand : null;
            _noticeTimer?.Stop();
            _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(onClick != null ? 8 : 5) };
            _noticeTimer.Tick += (_, _) =>
            {
                _noticeTimer.Stop();
                FadeOutAndCollapse(NoticeBar, 240, () =>   // 离开 240ms（DurSlow）
                {
                    _noticeClick = null;
                    NoticeBar.IsHitTestVisible = false;
                });
            };
            _noticeTimer.Start();
        }
        catch { }
    }

    /// <summary>点击提示条：执行其携带的跳转动作</summary>
    private void NoticeBar_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var act = _noticeClick;
        _noticeClick = null;
        try { _noticeTimer?.Stop(); } catch { }
        try { FadeOutAndCollapse(NoticeBar, 240); } catch { }
        act?.Invoke();
    }

    /// <summary>侧栏"图片库"未读小圆点：增量累加；进入图片库时清零</summary>
    private void AddImagesBadge(int n)
    {
        if (n <= 0) return;
        _imgBadgeCount += n;
        NavImagesBadgeText.Text = _imgBadgeCount > 99 ? "99+" : _imgBadgeCount.ToString();
        NavImagesBadge.Visibility = Visibility.Visible;
    }

    private void ClearImagesBadge()
    {
        _imgBadgeCount = 0;
        NavImagesBadge.Visibility = Visibility.Collapsed;
    }

    /// <summary>图片库「识别图片（人脸）」：同步新图 → imgtag 人脸扫描 → 刷新</summary>
    private async void ImgRecognize_Click(object sender, RoutedEventArgs e)
    {
        if (_recognizing) return;
        _recognizing = true;
        ImgRecognizeBtn.IsEnabled = false;
        ImgTagBtn.IsEnabled = false;
        var progress = new Action<string>(m => Dispatcher.Invoke(() => ImgRecognizeStatus.Text = m));
        try
        {
            progress.Invoke("准备人脸引擎…");
            // 人脸检测只用 C# ONNX，**不依赖** LM Studio / AI 打标引擎
            string syncMsg = "";
            await Task.Run(() => _recognizer.SyncNewImages(out syncMsg));
            if (!string.IsNullOrEmpty(syncMsg)) ImgRecognizeStatus.Text = syncMsg;

            var result = await Task.Run(() => _recognizer.ScanFacesAsync(null, progress));
            if (!result.Ok)
            {
                ImgRecognizeStatus.Text = "人脸识别失败：" + result.Error;
                _tray?.Notify("PictureButler", "人脸识别失败：" + result.Error);
                return;
            }
            // 悬空人脸归类，保证人物页完整
            int reassigned = 0;
            if (_img != null) await Task.Run(() => reassigned = _img.ReassignOrphanFaces());

            ImgRecognizeStatus.Text = $"识别完成：扫描 {result.Scanned} 张，检出 {result.Faces} 张人脸" +
                (reassigned > 0 ? $"，归类 {reassigned} 张" : "");
            ShowNotice($"人脸/人物已更新：{result.Faces} 张人脸，点左侧「人物」查看");
            _tray?.Notify("PictureButler · 人脸识别", $"完成：{result.Faces} 张人脸");

            // 直接切到人物页，让用户立刻看到结果（无需再点 AI 打标）
            await RefreshImgLibraryAsync();
            if (ImgTabPersons != null) ImgTabPersons.IsChecked = true;
            else _ = LoadImgPageAsync();
            UpdateFaceScanStats();
            UpdateFaceThumbStats();
        }
        catch (Exception ex)
        {
            ImgRecognizeStatus.Text = "识别出错：" + ex.Message;
            _tray?.Notify("PictureButler", "人脸识别出错：" + ex.Message);
        }
        finally
        {
            _recognizing = false;
            ImgRecognizeBtn.IsEnabled = true;
            ImgTagBtn.IsEnabled = true;
        }
    }

    /// <summary>图片库「AI 打标」：需 LM Studio（127.0.0.1:1234）</summary>
    private async void ImgTag_Click(object sender, RoutedEventArgs e) => await RunTagAsync(force: false, notifyTray: true);

    /// <summary>设置页：仅打标未打标的图片</summary>
    private async void TagPending_Click(object sender, RoutedEventArgs e) => await RunTagAsync(force: false, notifyTray: false);

    /// <summary>设置页：全部重新打标（忽略已有结果，耗时很长，可中断）</summary>
    private async void TagAll_Click(object sender, RoutedEventArgs e) => await RunTagAsync(force: true, notifyTray: false);

    /// <summary>停止当前 AI 打标任务（在图片之间生效：当前这张会跑完）</summary>
    private void TagStop_Click(object sender, RoutedEventArgs e)
    {
        try { ImgtagNative.Cancel(); } catch { }
        SetTagStatus("正在停止…（当前图片处理完成后中断）");
        if (TagStopBtn != null) TagStopBtn.IsEnabled = false;
        if (ImgTagStopBtn != null) ImgTagStopBtn.IsEnabled = false;
    }

    /// <summary>同步更新两处打标状态文本（工具条 + 设置页）</summary>
    private void SetTagStatus(string msg)
    {
        if (TagStatus != null) TagStatus.Text = msg;
        if (ImgRecognizeStatus != null) ImgRecognizeStatus.Text = msg;
    }

    /// <summary>统一的 AI 打标入口。<paramref name="force"/>=true 表示对全部图片重新打标。</summary>
    private async Task RunTagAsync(bool force, bool notifyTray)
    {
        if (_recognizing) { SetTagStatus("已有识别 / 打标任务正在进行"); return; }
        if (_recognizer == null) { SetTagStatus("识别服务未初始化"); return; }

        if (force)
        {
            var ok = MessageBox.Show(this,
                "「全部重新打标」会忽略已有结果，对所有图片重新生成描述与标签。\n\n" +
                "按单张约 20 秒估算，图片较多时可能耗时数小时；任务进行中可以随时点「停止」中断，\n" +
                "已完成的部分会保留。\n\n是否继续？",
                "全部重新打标", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (ok != MessageBoxResult.Yes) return;
        }

        _recognizing = true;
        ImgRecognizeBtn.IsEnabled = false;
        ImgTagBtn.IsEnabled = false;
        if (TagAllBtn != null) TagAllBtn.IsEnabled = false;
        if (TagStopBtn != null) { TagStopBtn.Visibility = Visibility.Visible; TagStopBtn.IsEnabled = true; }
        if (ImgTagStopBtn != null) { ImgTagStopBtn.Visibility = Visibility.Visible; ImgTagStopBtn.IsEnabled = true; }

        var progress = new Action<string>(m => Dispatcher.Invoke(() => SetTagStatus(m)));
        try
        {
            progress.Invoke("检查 LM Studio…");
            if (!_recognizer.ServerRunning())
            {
                if (!_recognizer.EnsureServer(out _)) { SetTagStatus("识别服务不可用"); return; }
            }
            bool lmOk = false;
            await Task.Run(() => lmOk = _recognizer.LmStudioAvailable());
            if (!lmOk)
            {
                SetTagStatus("AI 打标需要 LM Studio（127.0.0.1:1234），当前未运行");
                if (notifyTray) _tray?.Notify("PictureButler", "AI 打标需要 LM Studio（127.0.0.1:1234）");
                return;
            }

            // 自建索引里的新图先入识别库，否则打标队列里没有它们（点「AI 打标」会空转）
            string syncMsg = "";
            await Task.Run(() => _recognizer.SyncNewImages(out syncMsg));

            if (force)
            {
                progress.Invoke("全部重新打标中…");
            }
            else
            {
                // 把「无描述」的图重置为 pending；可能已有 pending 队列，所以 marked=0 也要继续跑
                int marked = 0;
                await Task.Run(() => marked = _recognizer.MarkPendingForTagging());
                progress.Invoke(marked > 0 ? $"AI 打标中…（{marked} 张待打标）" : "AI 打标中…");
            }

            // force 时 limit=0 表示不限数量（Rust 侧 limit>0 才截断）；增量打标一次跑完全部 pending
            var result = await Task.Run(() => _recognizer.AiTagAsync(null, 0, progress, force));
            if (!result.Ok)
            {
                SetTagStatus("AI 打标失败：" + result.Error);
                if (notifyTray) _tray?.Notify("PictureButler", "AI 打标失败：" + result.Error);
                return;
            }
            if (result.Scanned == 0 && result.Failed == 0)
            {
                SetTagStatus("没有待打标的图片");
            }
            else
            {
                SetTagStatus($"AI 打标完成：处理 {result.Scanned} 张（失败 {result.Failed}）");
                ShowNotice($"AI 打标完成：{result.Scanned} 张");
                if (notifyTray) _tray?.Notify("PictureButler · AI 打标", $"完成：处理 {result.Scanned} 张（失败 {result.Failed}）");
            }
            await RefreshImgLibraryAsync();
        }
        catch (Exception ex)
        {
            SetTagStatus("AI 打标出错：" + ex.Message);
        }
        finally
        {
            _recognizing = false;
            ImgRecognizeBtn.IsEnabled = true;
            ImgTagBtn.IsEnabled = true;
            if (TagAllBtn != null) TagAllBtn.IsEnabled = true;
            if (TagStopBtn != null) TagStopBtn.Visibility = Visibility.Collapsed;
            if (ImgTagStopBtn != null) ImgTagStopBtn.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>识别后刷新图片库全部视图（查看器删除后也会调用）</summary>
    internal     async Task RefreshImgLibraryAsync()
    {
        if (_img == null) RebuildImgService();
        _imgPage = 0;
        _personPage = 0;
        _tagPage = 0;
        // 库内容变化（新增/移除文件夹、删图）后，来源筛选列表也要跟着刷新
        try { await FillFolderFilterAsync(); } catch { }
        await LoadImgPageAsync();
    }

    // 卡片预览行高（DP 驱动模板绑定，避免运行时切换 DataTemplate 的渲染坑）
    public static readonly System.Windows.DependencyProperty CardPreviewMaxHeightProperty =
        System.Windows.DependencyProperty.Register(nameof(CardPreviewMaxHeight), typeof(double), typeof(MainWindow),
            new System.Windows.FrameworkPropertyMetadata(38.0));
    public double CardPreviewMaxHeight
    {
        get => (double)GetValue(CardPreviewMaxHeightProperty);
        set => SetValue(CardPreviewMaxHeightProperty, value);
    }

    /// <summary>
    /// 提示词「图片墙」视图开关。用 DP 驱动模板内的 DataTrigger，
    /// 避免运行时切换 DataTemplate 带来的渲染问题（与 CardPreviewMaxHeight 同一思路）。
    /// true = 卡片只显示图片（多列，靠右侧详情面板看完整信息）；false = 常规列表卡片。
    /// </summary>
    public static readonly System.Windows.DependencyProperty PromptWallModeProperty =
        System.Windows.DependencyProperty.Register(nameof(PromptWallMode), typeof(bool), typeof(MainWindow),
            new System.Windows.FrameworkPropertyMetadata(false));
    public bool PromptWallMode
    {
        get => (bool)GetValue(PromptWallModeProperty);
        set => SetValue(PromptWallModeProperty, value);
    }

    private void CardSize_Changed(object sender, RoutedEventArgs e)
    {
        if (PromptGrid == null) return;
        var size = CardCompact.IsChecked == true ? "compact"
                 : CardRoomy.IsChecked == true ? "roomy" : "standard";
        if (_settings.CardSize == size) return;
        _settings.CardSize = size;
        _settings.Save();
        CardPreviewMaxHeight = size switch { "compact" => 19.0, "roomy" => 57.0, _ => 38.0 };
    }

    /// <summary>提示词视图切换（列表 / 图片墙）。由工具条内联分段触发，持久化到设置。</summary>
    private void PromptView_Changed(object sender, RoutedEventArgs e)
    {
        if (PromptGrid == null || PromptViewList == null || PromptViewWall == null) return;
        bool wall = PromptViewWall.IsChecked == true;
        if (_settings.PromptWallMode == wall && PromptWallMode == wall) return;
        _settings.PromptWallMode = wall;
        _settings.Save();
        PromptWallMode = wall;
        ShowNotice(wall ? "已切换到图片墙视图" : "已切换到列表视图");
    }

    /// <summary>
    /// 主题切换。主题字典在 App 启动时装载（StaticResource 只解析一次），
    /// 所以这里保存设置后必须重启新实例才能生效 —— 详见 ThemeManager 的说明。
    /// </summary>
    private void Theme_Changed(object sender, RoutedEventArgs e)
    {
        if (_applyingSettings || _settings == null || ThemeLight == null) return;
        var want = ThemeLight.IsChecked == true ? ThemeManager.Light : ThemeManager.Dark;
        if (string.Equals(_settings.Theme, want, StringComparison.OrdinalIgnoreCase)) return;

        _settings.Theme = want;
        _settings.Save();

        var tip = want == ThemeManager.Light ? "浅色主题" : "深色主题";
        var r = MessageBox.Show(this,
            $"已保存为{tip}。\n\n主题在启动时装载，需要重启 PictureButler 才能生效。\n正在进行的扫描 / 打标任务会被中断。\n\n现在重启吗？",
            "PictureButler · 界面主题", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return;

        if (!ThemeManager.LaunchRelaunch())
        {
            ShowNotice($"{tip}已保存，请手动重启 PictureButler 生效");
            return;
        }
        // 先起新实例再关旧实例；新实例带 --relaunch，会等旧实例释放互斥量
        try { Close(); } catch { }
        System.Windows.Application.Current.Shutdown();
    }

    /// <summary>按设置初始化提示词视图（启动时调用一次）</summary>
    private void ApplyPromptViewSetting()
    {
        if (PromptViewList == null || PromptViewWall == null) return;
        bool wall = _settings.PromptWallMode;
        PromptViewWall.IsChecked = wall;
        PromptViewList.IsChecked = !wall;
        PromptWallMode = wall;
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private void BrowseImgDb_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 imgtag 数据库",
            Filter = "SQLite 数据库 (*.db;*.sqlite)|*.db;*.sqlite|所有文件 (*.*)|*.*",
            FileName = ImgDbPathBox.Text
        };
        if (dlg.ShowDialog(this) == true)
        {
            ImgDbPathBox.Text = dlg.FileName;
            SaveImgDb_Click(sender, e);
        }
    }

    // ---- 图片文件夹来源 ----

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择图片文件夹" };
        if (dlg.ShowDialog(this) == true)
        {
            var path = dlg.FolderName;
            if (!_settings.ImageFolders.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                _settings.ImageFolders.Add(path);
                _settings.Save();
                SyncImgAllowedRoots();
                RefreshFolderList();
            }
        }
    }

    /// <summary>设置页：清理「索引有记录、磁盘文件已不存在」的残留索引</summary>
    private async void CleanMissingIndex_Click(object sender, RoutedEventArgs e)
    {
        if (_img == null) RebuildImgService();
        CleanMissingStatus.Text = "统计中…";
        TintStatus(CleanMissingStatus, null);
        int n = 0;
        try { n = await Task.Run(() => _img.CountMissingImages()); } catch { }
        if (n <= 0)
        {
            CleanMissingStatus.Text = "没有失效记录";
            TintStatus(CleanMissingStatus, true);
            return;
        }
        var ok = MessageBox.Show(this,
            $"索引中有 {n} 条记录对应的文件已不存在（通常是因为文件夹被删除或移动）。\n\n" +
            "是否清理这些失效索引？\n" +
            "· 只删除索引记录，不动磁盘文件\n" +
            "· 「来源」筛选里对应的目录会一并消失\n\n" +
            "注意：如果部分照片所在的移动硬盘当前未连接，请先连接后再操作，避免误清。",
            "清理失效图片索引", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (ok != MessageBoxResult.Yes)
        {
            CleanMissingStatus.Text = "已取消";
            TintStatus(CleanMissingStatus, null);
            return;
        }
        int removed = 0;
        try { removed = await Task.Run(() => _img.RemoveMissingIndex()); } catch { }
        CleanMissingStatus.Text = $"已清理 {removed} 条失效索引";
        TintStatus(CleanMissingStatus, true);
        await RefreshImgLibraryAsync();   // 含来源列表刷新
        UpdateFaceScanStats();
        ShowNotice($"已清理 {removed} 条失效图片索引");
    }

    /// <summary>设置页：移除图片文件夹来源。
    /// 同时提供「清理该文件夹的图片索引」选项 —— 否则来源筛选里会残留已移除的目录
    /// （来源列表取自「已入库图片所在的目录」，不清索引就会一直存在）。</summary>
    private async void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string path) return;
        if (_img == null) RebuildImgService();

        int n = 0;
        try { n = await Task.Run(() => _img.CountImagesInFolder(path)); } catch { }

        var choice = MessageBoxResult.Yes;
        if (n > 0)
        {
            choice = MessageBox.Show(this,
                $"移除文件夹来源：\n{path}\n\n该文件夹下有 {n} 张图片已入库。\n\n" +
                "【是】同时移除这些图片的索引 —— 磁盘文件不受影响\n" +
                "【否】仅停止扫描；图库将不再显示该目录（磁盘与索引记录保留）\n" +
                "【取消】不做任何修改",
                "移除文件夹", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (choice == MessageBoxResult.Cancel) return;
        }

        _settings.ImageFolders.Remove(path);
        // 用户显式移除来源，允许存成空
        _settings.AllowEmptyImageFolders = _settings.ImageFolders.Count == 0;
        _settings.Save();
        SyncImgAllowedRoots();

        int removed = 0;
        if (choice == MessageBoxResult.Yes && n > 0)
        {
            try { removed = await Task.Run(() => _img.RemoveFolderIndex(path)); } catch { }
        }

        RefreshFolderList();

        // 来源筛选列表要重新加载，否则会残留已移除文件夹的芯片
        try
        {
            _imgFoldersLoaded = false;
            await FillFolderFilterAsync();
        }
        catch { }

        if (_imgMode == "all") await LoadImgPageAsync();
        UpdateFaceScanStats();

        ShowNotice(removed > 0
            ? $"已移除文件夹，并清理 {removed} 张图片索引"
            : "已移除文件夹来源");
    }

    private void RefreshFolderList()
    {
        FolderList.ItemsSource = null;
        FolderList.ItemsSource = _settings.ImageFolders.ToList();
        ScanStatus.Text = _settings.ImageFolders.Count > 0 ? $"已配置 {_settings.ImageFolders.Count} 个文件夹" : "未配置文件夹";
        DataInfoText.Text =
            "提示词库：%APPDATA%\\com.picturebutler.app\\prompts.db\n" +
            "图片文件夹：" + (_settings.ImageFolders.Count > 0 ? string.Join("；", _settings.ImageFolders) : "未配置") + "\n" +
            "识别库：" + _settings.ImgtagDbPath + "\n" +
            "本地服务：" + (_http?.Port > 0 ? $"127.0.0.1:{_http.Port}" : "未启动");
    }

    /// <summary>设置页：清理空人物（先报数、二次确认，执行后若在人物页则刷新）</summary>
    private async void PrunePersons_Click(object sender, RoutedEventArgs e)
    {
        if (_img == null) RebuildImgService();
        PruneStatus.Text = "统计中…";
        TintStatus(PruneStatus, null);
        var empty = await Task.Run(() => _img.CountEmptyPersons());
        if (empty <= 0)
        {
            PruneStatus.Text = "没有需要清理的空人物";
            TintStatus(PruneStatus, null);
            return;
        }
        var ok = MessageBox.Show(this,
            $"检测到 {empty} 个没有任何有效人脸的空人物，是否删除？\n（你主动忽略的人物不会被删除）",
            "清理空人物", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ok != MessageBoxResult.Yes) { PruneStatus.Text = "已取消"; TintStatus(PruneStatus, null); return; }
        var removed = await Task.Run(() => _img.PruneEmptyPersons());
        PruneStatus.Text = $"已清理 {removed} 个空人物";
        TintStatus(PruneStatus, true);
        if (_imgMode == "persons") _ = LoadImgPageAsync();
    }

    private void ClearThumbCache_Click(object sender, RoutedEventArgs e)
    {
        var (count, bytes) = ThumbCache.Stats();
        if (count == 0) { ThumbCacheStatus.Text = "缓存已是空的"; TintStatus(ThumbCacheStatus, null); return; }
        var mb = bytes / 1024.0 / 1024.0;
        var ok = MessageBox.Show(this,
            $"缩略图缓存共 {count} 个文件、约 {mb:F1} MB，是否全部清空？\n清空后下次浏览图片库会重新生成。",
            "清空缩略图缓存", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ok != MessageBoxResult.Yes) { ThumbCacheStatus.Text = "已取消"; TintStatus(ThumbCacheStatus, null); return; }
        var removed = ThumbCache.Clear();
        ThumbCacheStatus.Text = $"已清空 {removed} 个缓存文件";
        TintStatus(ThumbCacheStatus, true);
        if (_imgMode is "all" or "personPhotos" or "tagPhotos" or "idPhotos" or "docPhotos") _ = LoadImgPageAsync();
    }

    private async void ScanFolders_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.ImageFolders.Count == 0)
        {
            ScanStatus.Text = "请先添加文件夹";
            TintStatus(ScanStatus, false);
            return;
        }
        if (_img == null) RebuildImgService();
        ScanStatus.Text = "扫描中…";
        TintStatus(ScanStatus, null);
        var progress = new Progress<string>(s => ScanStatus.Text = s);
        try
        {
            var added = await _img.ScanFoldersAsync(_settings.ImageFolders, progress);
            ScanStatus.Text = $"扫描完成（新增 {added}）";
            TintStatus(ScanStatus, true);
            if (NavImages.IsChecked == true)
            {
                _imgPage = 0;
                await LoadImgPageAsync();
            }
            // 自动识别人脸（如果开启且有新增图片）
            if (added > 0 && _settings.FaceAutoRecognize && _recognizer != null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (_recognizer.EnsureServer(out _))
                        {
                            _recognizer.SyncNewImages(out _);
                            var r = await _recognizer.ScanFacesAsync(null, null);
                            Dispatcher.Invoke(() =>
                            {
                                ShowNotice(r.Faces > 0
                                    ? $"自动识别完成：新增 {r.Faces} 张人脸"
                                    : "自动识别完成：未检出人脸");
                                UpdateFaceScanStats();
                                UpdateFaceThumbStats();
                            });
                        }
                    }
                    catch { }
                });
            }
        }
        catch (Exception ex)
        {
            ScanStatus.Text = "扫描失败：" + ex.Message;
            TintStatus(ScanStatus, false);
            System.Diagnostics.Debug.WriteLine("ScanFolders: " + ex);
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "pb_scan_error.txt"), ex.ToString());
        }
    }

    private async void SaveImgDb_Click(object sender, RoutedEventArgs e)
    {
        var path = ImgDbPathBox.Text.Trim();
        ImgDbStatus.Text = "测试中…";
        TintStatus(ImgDbStatus, null);
        var err = await Task.Run(() => ImgtagService.TestPath(path));
        if (err != null)
        {
            ImgDbStatus.Text = "无效：" + err;
            TintStatus(ImgDbStatus, false);
            return;
        }
        _settings.ImgtagDbPath = path;
        _settings.Save();
        RebuildImgService();
        _imgLoaded = false;
        _imgPage = 0;
        ImgDbStatus.Text = "已保存";
        TintStatus(ImgDbStatus, true);
        DataInfoText.Text =
            "提示词库：%APPDATA%\\com.picturebutler.app\\prompts.db\n" +
            "图片文件夹：" + (_settings.ImageFolders.Count > 0 ? string.Join("；", _settings.ImageFolders) : "未配置") + "\n" +
            "识别库：" + path + "\n" +
            "本地服务：" + (_http?.Port > 0 ? $"127.0.0.1:{_http.Port}" : "未启动");
        if (NavImages.IsChecked == true)
        {
            await LoadImgPageAsync();
        }
    }

    private void RebuildImgService()
    {
        var indexPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "com.picturebutler.app", "image_index.db");
        _img = new ImageLibraryService(indexPath, _settings.ImgtagDbPath)
        {
            // 图库只展示已添加来源下的图片
            AllowedRoots = _settings.ImageFolders.ToList()
        };
    }

    /// <summary>来源变更后同步 AllowedRoots，保证列表只显示已添加来源。</summary>
    private void SyncImgAllowedRoots()
    {
        if (_img == null) RebuildImgService();
        else _img.AllowedRoots = _settings.ImageFolders.ToList();
    }

    private async void PageSize_Changed(object sender, RoutedEventArgs e)
    {
        if (ImgGrid == null) return;
        var size = Page30.IsChecked == true ? 30 : Page90.IsChecked == true ? 90 : 60;
        if (_settings.ImgPageSize == size) return;
        _settings.ImgPageSize = size;
        _settings.Save();
        _imgPage = 0;
        if (NavImages.IsChecked == true)
        {
            await LoadImgPageAsync();
        }
    }

    // ==================== 图片库视图 ====================

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        // XAML 解析阶段 IsChecked=True 会提前触发，此时子控件可能尚未构造
        if (PromptView == null || ImgView == null || SettingsView == null) return;
        bool navId = NavIdPhotos?.IsChecked == true;
        bool navDoc = NavDocs?.IsChecked == true;
        var showImages = NavImages.IsChecked == true || navId || navDoc;
        var showSettings = NavSettings.IsChecked == true;
        PromptView.Visibility = (showImages || showSettings) ? Visibility.Collapsed : Visibility.Visible;
        ImgView.Visibility = showImages ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = showSettings ? Visibility.Visible : Visibility.Collapsed;
        if (showSettings)
        {
            // 延迟到 UI 空闲时刷新人脸识别统计，避免布局期间异常
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (FaceThumbStats != null) UpdateFaceThumbStats();
                    if (_img != null && FaceScanStats != null) UpdateFaceScanStats();
                    if (_img != null && IgnoredPersonList != null) RefreshIgnoredPersons();
                    if (_recognizer != null && FaceEngineDot != null && FaceEngineStatus != null)
                    {
                        var ready = _recognizer.ServerRunning();
                        var brushKey = ready ? "SuccessBrush" : "DangerBrush";
                        var res = Application.Current.FindResource(brushKey);
                        if (res is System.Windows.Media.Brush b) FaceEngineDot.Fill = b;
                        FaceEngineStatus.Text = ready ? "识别引擎已就绪" : "识别引擎未启动";
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("设置页人脸统计刷新失败: " + ex.Message);
                }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }
        if (showImages) ClearImagesBadge();
        // 右侧详情面板只在提示词页占用空间；切到图片库/设置时收起并释放宽度，
        // 让图片库/设置充分利用整个内容区
        if (showImages || showSettings)
        {
            if (DetailCol != null) DetailCol.Width = new GridLength(0);
            ShowDetail(null);
        }
        else
        {
            if (DetailCol != null) DetailCol.Width = new GridLength(360);
            if (_selectedItem != null) ShowDetail(_selectedItem);
        }
        if (showImages && !_imgLoaded)
        {
            _imgLoaded = true;
            RebuildImgService();
            _imgFilterReady = true;
            SetFilterBarVisibility();
            _ = FillFolderFilterAsync();
            _ = LoadImgPageAsync();
        }
        // 一级导航：证件照 / 文档（独立分类页）
        if (navId || navDoc)
        {
            OpenCategoryView(navId ? "idPhotos" : "docPhotos");
        }
        else if (NavImages.IsChecked == true && _imgMode is "idPhotos" or "docPhotos")
        {
            // 从分类回到图片库 → 回到「全部」。
            // **必须先把 _imgMode 复位为 all**（0.62.4）：ImgTab_Changed 开头有一条拦截
            // 「当前是 idPhotos/docPhotos 且分类导航仍选中 → 直接 return」，那本来是为了防止
            // 分类页的页签误改模式；但从分类返回图片库时若命中它，模式就永远卡在 idPhotos，
            // 界面会继续显示分类里那十几张 —— 表现就是「图片库只剩证件照里的图片」。
            _imgMode = "all";
            // 直接调用页签切换逻辑，而不是改 IsChecked：
            // 分类模式下 ImgTabAll 很可能**本来就是选中态**，再赋 true 不会触发 Checked 事件，
            // 界面就会一直停留在分类那十几张上（0.62.4）
            if (ImgTabAll != null) ImgTab_Changed(ImgTabAll, new RoutedEventArgs());
            else _ = LoadImgPageAsync();
        }
        // 从其他视图切回提示词：强制重建卡片（可见性切换后 ItemsControl 可能不刷新内容）
        if (!showImages && !showSettings)
        {
            PromptGrid.ItemsSource = null;
            ApplyFilter();
        }
    }

    /// <summary>一级导航进入证件照 / 文档分类页。</summary>
    private void OpenCategoryView(string mode)
    {
        _imgPage = 0;
        _imgMode = mode;
        ImgBackBtn.Visibility = Visibility.Collapsed;
        ImgSearchBox.Visibility = Visibility.Collapsed;
        TagSearchBox.Visibility = Visibility.Collapsed;
        TagActionBar.Visibility = Visibility.Visible;
        TagNameText.Text = mode == "idPhotos" ? "证件照" : "文档";
        TagAndBtn.Visibility = Visibility.Collapsed;
        TagClearAndBtn.Visibility = Visibility.Collapsed;
        ApplyFilterBarVisibility(forceCollapsed: true);
        ImgMultiBtn.Visibility = Visibility.Visible;
        PersonMultiBtn.Visibility = Visibility.Collapsed;
        if (MergeSuggestBtn != null) MergeSuggestBtn.Visibility = Visibility.Collapsed;
        if (!_imgLoaded)
        {
            _imgLoaded = true;
            RebuildImgService();
            _imgFilterReady = true;
        }
        _ = LoadImgPageAsync();
    }

    private void ImgTab_Changed(object sender, RoutedEventArgs e)
    {
        if (ImgGrid == null || PersonGrid == null || TagGrid == null) return;
        // 证件照 / 文档已改为左侧一级导航，这里只处理 图片库内页签
        if ((_imgMode is "idPhotos" or "docPhotos") && (NavIdPhotos?.IsChecked == true || NavDocs?.IsChecked == true))
            return;
        _imgPage = 0;
        _personPage = 0;
        _tagPage = 0;
        _imgMode = ImgTabAll.IsChecked == true ? "all"
                 : ImgTabPersons.IsChecked == true ? "persons"
                 : "tags";
        ImgBackBtn.Visibility = Visibility.Collapsed;
        ImgSearchBox.Visibility = _imgMode == "all" ? Visibility.Visible : Visibility.Collapsed;
        TagSearchBox.Visibility = _imgMode == "tags" ? Visibility.Visible : Visibility.Collapsed;
        TagActionBar.Visibility = Visibility.Collapsed;
        TagAndBtn.Visibility = Visibility.Visible;

        // 多选按钮：全部/人物照片页 → 图片多选；人物列表 → 人物多选
        bool isPersonList = _imgMode == "persons";
        ImgMultiBtn.Visibility = isPersonList ? Visibility.Collapsed : Visibility.Visible;
        PersonMultiBtn.Visibility = isPersonList ? Visibility.Visible : Visibility.Collapsed;
        if (MergeSuggestBtn != null)
            MergeSuggestBtn.Visibility = isPersonList ? Visibility.Visible : Visibility.Collapsed;
        LogDebug($"ImgTab_Changed: mode={_imgMode} isPersonList={isPersonList} PersonMultiBtn.Vis={PersonMultiBtn.Visibility} ImgMultiBtn.Vis={ImgMultiBtn.Visibility}");
        // 切 Tab 时退出多选模式
        if (_personMulti && !isPersonList) { _personMulti = false; UpdatePersonMultiUI(); }
        if (_imgMulti && isPersonList) { _imgMulti = false; UpdateImgMultiUI(); }

        SetFilterBarVisibility();
        // 每次切到「全部」都重建来源列表：只加载一次会导致移除文件夹后来源里残留旧目录
        if (_imgMode == "all") _ = FillFolderFilterAsync();
        _ = LoadImgPageAsync();
    }

    // ---- 筛选（全部为内联控件、无弹层，杜绝弹层点击穿透到下方网格） ----

    /// <summary>识别状态分段选择变化：重置到第一页并用新条件下推查询。</summary>
    private void ImgStatusChip_Checked(object sender, RoutedEventArgs e)
    {
        if (!_imgFilterReady) return;
        _imgStatus = (sender as FrameworkElement)?.Tag as string ?? "all";
        _imgPage = 0;
        _ = LoadImgPageAsync();
    }

    private static string ShortFolderName(string path)
    {
        var s = path.TrimEnd('\\', '/');
        var i = s.LastIndexOfAny(new[] { '\\', '/' });
        var leaf = i >= 0 ? s[(i + 1)..] : s;
        return leaf.Length > 12 ? leaf[..12] : leaf;
    }

    /// <summary>排序分段选择变化。</summary>
    private void ImgSortChip_Checked(object sender, RoutedEventArgs e)
    {
        if (!_imgFilterReady) return;
        _imgSort = (sender as FrameworkElement)?.Tag as string ?? "date_desc";
        _imgPage = 0;
        _ = LoadImgPageAsync();
    }

    /// <summary>来源分段选择变化。</summary>
    private void ImgFolderChip_Checked(object sender, RoutedEventArgs e)
    {
        if (!_imgFilterReady) return;
        _imgFolder = (sender as FrameworkElement)?.Tag as string ?? "";
        _imgPage = 0;
        _ = LoadImgPageAsync();
    }

    /// <summary>按已入库文件夹生成「来源」选项（"" = 全部，放在首位）。</summary>
    private void UpdateFolderChips()
    {
        ImgFolderChipPanel.Children.Clear();
        if (_imgFolderList.Count == 0) _imgFolderList.Add("");
        foreach (var f in _imgFolderList)
        {
            var rb = new System.Windows.Controls.RadioButton
            {
                Content = string.IsNullOrEmpty(f) ? "全部" : ShortFolderName(f),
                GroupName = "imgFolder",
                Tag = f,
                Style = (Style)FindResource("FilterChip"),
                IsChecked = string.Equals(f, _imgFolder, StringComparison.Ordinal),
                ToolTip = string.IsNullOrEmpty(f) ? "全部来源" : f
            };
            rb.Checked += ImgFolderChip_Checked;
            ImgFolderChipPanel.Children.Add(rb);
        }
    }

    /// <summary>来源列表一次性加载（"" = 全部，放在首位），保留当前选择。</summary>
    private async Task FillFolderFilterAsync()
    {
        _imgFoldersLoaded = true;
        var folders = await Task.Run(() => _img.DistinctFolders());
        await Dispatcher.InvokeAsync(() =>
        {
            _imgFilterReady = false;
            _imgFolderList = new List<string> { "" };
            _imgFolderList.AddRange(folders);
            if (!_imgFolderList.Contains(_imgFolder)) _imgFolder = "";
            UpdateFolderChips();
            _imgFilterReady = true;
        });
    }

    /// <summary>筛选下拉与标签芯片只在“全部”网格显示。</summary>
    private void SetFilterBarVisibility()
    {
        ApplyFilterBarVisibility();
        RenderTagChips();
    }

    /// <summary>
    /// 筛选行显隐统一入口（0.61.0 批次 2b）：仅「全部」模式且用户已展开时可见，默认收起；
    /// 其余模式整组隐藏，切换按钮随之显隐。forceCollapsed 用于旁路强制收起的场景。
    /// </summary>
    private void ApplyFilterBarVisibility(bool forceCollapsed = false)
    {
        if (ImgFilterGroup == null || ImgFilterToggle == null) return;
        var showAll = _imgMode == "all";
        var visible = showAll && _imgFilterExpanded && !forceCollapsed;
        ImgFilterGroup.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ImgFilterToggle.Visibility = showAll ? Visibility.Visible : Visibility.Collapsed;
        ImgFilterToggle.Content = visible ? "筛选 ▴" : "筛选 ▾";
    }

    /// <summary>筛选行开关（批次 2b）：以内联方式展开 / 收起，不违反红线 R1（禁止弹层）。</summary>
    private void ImgFilterToggle_Click(object sender, RoutedEventArgs e)
    {
        _imgFilterExpanded = ImgFilterGroup.Visibility != Visibility.Visible;
        ApplyFilterBarVisibility();
    }

    /// <summary>
    /// 设置页二级导航（0.61.0 批次 3）：滚动定位到对应区块。
    /// XAML 解析期 IsChecked="True" 触发 Checked 时其余区块尚未构造，需判空忽略。
    /// </summary>
    private void SetNav_Checked(object sender, RoutedEventArgs e)
    {
        if (SetSecAppearance == null) return;
        FrameworkElement? target = ((FrameworkElement)sender).Name switch
        {
            "SetNavAppearance" => SetSecAppearance,
            "SetNavPrompts"    => SetSecPrompts,
            "SetNavLibrary"    => SetSecLibrary,
            "SetNavFace"       => SetSecFace,
            "SetNavTagging"    => SetSecTagging,
            "SetNavIgnored"    => SetSecIgnored,
            "SetNavComfy"      => SetSecComfy,
            "SetNavData"       => SetSecData,
            "SetNavAbout"      => SetSecAbout,
            _ => null,
        };
        if (target != null && target.IsLoaded)
            target.BringIntoView();
    }

    private void ImgBack_Click(object sender, RoutedEventArgs e)
    {
        if (_imgMode == "personPhotos")
        {
            _imgMode = "persons";
            _personPage = 0;
            ImgTabPersons.IsChecked = true;
            HidePersonActionBar();
        }
        else if (_imgMode == "tagPhotos")
        {
            _imgMode = "tags";
            _tagPage = 0;
            ImgTabTags.IsChecked = true;
            HidePersonActionBar();
            TagActionBar.Visibility = Visibility.Collapsed;
        }
        ImgBackBtn.Visibility = Visibility.Collapsed;
        ImgSearchBox.Visibility = _imgMode == "all" ? Visibility.Visible : Visibility.Collapsed;
        TagSearchBox.Visibility = _imgMode == "tags" ? Visibility.Visible : Visibility.Collapsed;
        _ = LoadImgPageAsync();
    }

    private void PersonItem_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // 多选模式下，点击由覆盖层（PersonMultiCard_Click）处理，这里只处理正常模式
        if (_personMulti) return;
        if (sender is Border b && b.DataContext is PersonItem p)
        {
            LogDebug($"PersonItem_Click: person={p.Name} _personMulti={_personMulti} PersonMultiVisible={PersonMultiVisible}");
            _imgMode = "personPhotos";
            _currentPersonId = p.Id;
            _personPage = 0;
            ImgBackBtn.Visibility = Visibility.Visible;
            ShowPersonActionBar(p);
            _ = LoadImgPageAsync();
        }
    }

    /// <summary>多选模式下，点击卡片覆盖层切换勾选</summary>
    private void PersonMultiCard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is Border b && b.Tag is long id)
        {
            LogDebug($"PersonMultiCard_Click: id={id} _personMulti={_personMulti}");
            var item = CurrentPersonItems().FirstOrDefault(p => p.Id == id);
            if (item == null) return;
            item.IsSelected = !item.IsSelected;
            if (item.IsSelected) _personSel.Add(id);
            else _personSel.Remove(id);
            UpdatePersonSelCount();
            e.Handled = true;
        }
    }

    /// <summary>personPhotos 模式：显示人物操作栏，隐藏页签与全局按钮</summary>
    private void ShowPersonActionBar(PersonItem p)
    {
        PersonNameText.Text = p.Name;
        PersonFaceCountText.Text = $"{p.PhotoCount} 张照片";
        PersonActionBar.Visibility = Visibility.Visible;
        TagActionBar.Visibility = Visibility.Collapsed;
        ImgTabAll.Visibility = Visibility.Collapsed;
        ImgTabPersons.Visibility = Visibility.Collapsed;
        ImgTabTags.Visibility = Visibility.Collapsed;
        ImgMultiBtn.Visibility = Visibility.Visible;
        PersonMultiBtn.Visibility = Visibility.Collapsed;
        if (MergeSuggestBtn != null) MergeSuggestBtn.Visibility = Visibility.Collapsed;
        ImgRecognizeBtn.Visibility = Visibility.Collapsed;
        ImgTagBtn.Visibility = Visibility.Collapsed;
        // 批次 2c：进入人物详情时退出多选（否则多选态整条工具条会替换常规态，此处操作栏就点不到了）
        if (_imgMulti) { _imgMulti = false; UpdateImgMultiUI(); }
        ImgRecognizeStatus.Visibility = Visibility.Collapsed;
        ImgSearchBox.Visibility = Visibility.Collapsed;
        TagSearchBox.Visibility = Visibility.Collapsed;
        ApplyFilterBarVisibility(forceCollapsed: true);
        if (ImgTagChipPanel != null) ImgTagChipPanel.Visibility = Visibility.Collapsed;
    }

    /// <summary>退出 personPhotos / tagPhotos：恢复页签与全局按钮</summary>
    private void HidePersonActionBar()
    {
        PersonActionBar.Visibility = Visibility.Collapsed;
        ImgTabAll.Visibility = Visibility.Visible;
        ImgTabPersons.Visibility = Visibility.Visible;
        ImgTabTags.Visibility = Visibility.Visible;
        ImgMultiBtn.Visibility = Visibility.Collapsed;
        PersonMultiBtn.Visibility = Visibility.Visible;
        if (MergeSuggestBtn != null) MergeSuggestBtn.Visibility = Visibility.Visible;
        ImgRecognizeBtn.Visibility = Visibility.Visible;
        ImgTagBtn.Visibility = Visibility.Visible;
        ImgRecognizeStatus.Visibility = Visibility.Visible;
        SetFilterBarVisibility();
    }

    // ==================== 人物管理操作（重命名 / 换封面 / 合并 / 删除） ====================

    /// <summary>从右键菜单或当前 personPhotos 上下文解析人物 id</summary>
    private long? ResolvePersonId(object sender)
    {
        if (sender is FrameworkElement fe && fe.DataContext is PersonItem p) return p.Id;
        if (_imgMode == "personPhotos") return _currentPersonId;
        return null;
    }

    private PersonItem? CurrentPerson(long id)
        => _img.Persons().FirstOrDefault(x => x.Id == id);

    private void PersonRename_Click(object sender, RoutedEventArgs e)
    {
        var pid = ResolvePersonId(sender);
        if (pid == null) return;
        var p = CurrentPerson(pid.Value);
        var dlg = new InputBoxWindow("重命名人物", "新名称：", p?.Name ?? "人物") { Owner = this };
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.Value)) return;
        if (_img.RenamePerson(pid.Value, dlg.Value.Trim()))
        {
            if (_imgMode == "personPhotos") PersonNameText.Text = dlg.Value.Trim();
            _ = LoadImgPageAsync();
        }
        else
        {
            MessageBox.Show(this, "重命名失败（识别库不可用）", "重命名", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void PersonSetAvatar_Click(object sender, RoutedEventArgs e)
    {
        var pid = ResolvePersonId(sender);
        if (pid == null) return;
        var p = CurrentPerson(pid.Value);
        var faces = _img.PersonFaces(pid.Value);
        if (faces.Count == 0)
        {
            MessageBox.Show(this, "该人物暂无人脸可作封面，请先对包含此人的图片进行人脸识别。",
                "更换封面", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new FacePickerWindow(p?.Name ?? "人物", faces) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        if (_img.SetPersonAvatar(pid.Value, dlg.SelectedFaceId))
        {
            // 主动预热该人脸裁剪缓存，保证列表刷新后立即显示新封面（而不是先显示原图再跳变）
            var fid = dlg.SelectedFaceId;
            await Task.Run(() =>
            {
                try
                {
                    var faceFile = Path.Combine(_imgTagDir, ".faces", fid + ".jpg");
                    if (!File.Exists(faceFile))
                    {
                        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                        http.GetByteArrayAsync($"{ImgtagRecognizer.ServerUrl}/api/face-thumbnail/{fid}").GetAwaiter().GetResult();
                    }
                }
                catch { }
            });
            _ = LoadImgPageAsync();
        }
        else
        {
            MessageBox.Show(this, "设置封面失败（识别库不可用）", "更换封面", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void PersonMerge_Click(object sender, RoutedEventArgs e)
    {
        var pid = ResolvePersonId(sender);
        if (pid == null) return;
        var p = CurrentPerson(pid.Value);
        var others = _img.Persons().Where(x => x.Id != pid.Value).ToList();
        if (others.Count == 0)
        {
            MessageBox.Show(this, "没有其他人物可合并。", "合并人物", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new PersonPickerWindow(p?.Name ?? "人物", others, _imgTagDir) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var target = others.FirstOrDefault(o => o.Id == dlg.SelectedPersonId);
        var confirm = MessageBox.Show(this,
            $"确定把「{p?.Name}」合并到「{target?.Name}」？\n该人物的全部照片与封面将并入目标人物，此人物随即移除。",
            "合并人物", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;
        if (_img.MergePersons(new List<long> { pid.Value }, dlg.SelectedPersonId))
        {
            if (_imgMode == "personPhotos")
            {
                _imgMode = "persons";
                ImgTabPersons.IsChecked = true;
                HidePersonActionBar();
            }
            _ = LoadImgPageAsync();
        }
        else
        {
            MessageBox.Show(this, "合并失败（识别库不可用）", "合并人物", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void PersonDelete_Click(object sender, RoutedEventArgs e)
    {
        var pid = ResolvePersonId(sender);
        if (pid == null) return;
        var p = CurrentPerson(pid.Value);
        var confirm = MessageBox.Show(this,
            $"确定删除人物「{p?.Name}」？\n其 {p?.PhotoCount} 张照片将从人物列表移除（图片文件本身不受影响）。",
            "删除人物", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;
        var faceChoice = MessageBox.Show(this,
            "是否同时删除该人物的全部人脸识别数据？\n\n【是】删除人脸框 —— 相关图片会标记为待重扫，下次「识别人脸」可重新检测\n【否】保留人脸框 —— 自动归入其它人物，人物体系继续有效\n【取消】不做修改",
            "删除人物", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (faceChoice == MessageBoxResult.Cancel) return;
        bool delFaces = faceChoice == MessageBoxResult.Yes;
        if (_img.DeletePerson(pid.Value, delFaces))
        {
            if (_imgMode == "personPhotos")
            {
                _imgMode = "persons";
                ImgTabPersons.IsChecked = true;
                HidePersonActionBar();
            }
            _ = LoadImgPageAsync();
        }
        else
        {
            MessageBox.Show(this, "删除失败（识别库不可用）", "删除人物", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ==================== 人物多选 ====================

    private void PersonMulti_Click(object sender, RoutedEventArgs e)
    {
        _personMulti = !_personMulti;
        LogDebug($"PersonMulti_Click: _personMulti={_personMulti}");
        UpdatePersonMultiUI();
    }

    private void UpdatePersonMultiUI()
    {
        PersonMultiVisible = _personMulti;
        LogDebug($"UpdatePersonMultiUI: PersonMultiVisible={PersonMultiVisible} _personMulti={_personMulti}");
        // 同步所有人物项的多选模式显示
        foreach (var p in CurrentPersonItems()) p.MultiMode = _personMulti;
        if (_personMulti)
        {
            PersonMultiBar.Visibility = Visibility.Visible;
            PersonMultiBtn.Content = "退出多选";
        }
        else
        {
            PersonMultiBar.Visibility = Visibility.Collapsed;
            PersonMultiBtn.Content = "多选";
            _personSel.Clear();
            foreach (var p in CurrentPersonItems()) p.IsSelected = false;
        }
        UpdatePersonSelCount();
        ApplyImgToolbarMode();
    }

    private void PersonChk_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox chk && chk.Tag is long id)
        {
            LogDebug($"PersonChk_Click: id={id} IsChecked={chk.IsChecked} _personMulti={_personMulti}");
            if (chk.IsChecked == true) _personSel.Add(id);
            else _personSel.Remove(id);
            UpdatePersonSelCount();
        }
    }

    private void UpdatePersonSelCount()
    {
        PersonSelCount.Text = $"已选 {_personSel.Count} 人";
        PersonSelMergeBtn.IsEnabled = _personSel.Count >= 1;
        PersonSelDelBtn.IsEnabled = _personSel.Count >= 1;
    }

    private void PersonSelAll_Click(object sender, RoutedEventArgs e)
    {
        var items = CurrentPersonItems().ToList();
        bool allSelected = items.All(p => p.IsSelected);
        foreach (var p in items) p.IsSelected = !allSelected;
        _personSel.Clear();
        if (!allSelected) foreach (var p in items) _personSel.Add(p.Id);
        UpdatePersonSelCount();
    }

    private void PersonSelMerge_Click(object sender, RoutedEventArgs e)
    {
        if (_personSel.Count == 0) return;
        var others = _img.Persons().Where(x => !_personSel.Contains(x.Id)).ToList();
        if (others.Count == 0)
        {
            MessageBox.Show(this, "没有可作为合并目标的人物。", "合并人物", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new PersonPickerWindow($"{_personSel.Count} 个人物", others, _imgTagDir) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var target = others.FirstOrDefault(o => o.Id == dlg.SelectedPersonId);
        var sourceNames = string.Join("、", _img.Persons().Where(p => _personSel.Contains(p.Id)).Select(p => p.Name).Take(3));
        if (_personSel.Count > 3) sourceNames += $" 等 {_personSel.Count} 人";
        var confirm = MessageBox.Show(this,
            $"确定把「{sourceNames}」合并到「{target?.Name}」？\n这些人物的全部照片与封面将并入目标人物，原人物随即移除。",
            "合并人物", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;
        var sourceIds = _personSel.ToList();
        if (_img.MergePersons(sourceIds, dlg.SelectedPersonId))
        {
            _personMulti = false;
            UpdatePersonMultiUI();
            _ = LoadImgPageAsync();
            ShowNotice($"已合并 {sourceIds.Count} 个人物到「{target?.Name}」");
        }
        else
        {
            MessageBox.Show(this, "合并失败（识别库不可用）", "合并人物", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void PersonSelDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_personSel.Count == 0) return;
        var confirm = MessageBox.Show(this,
            $"确定删除选中的 {_personSel.Count} 个人物？\n仅删除人物分组，不会删除图片文件。",
            "删除人物", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;
        int ok = 0;
        foreach (var id in _personSel.ToList())
        {
            if (_img.DeletePerson(id, deleteFaces: false)) ok++;
        }
        _personMulti = false;
        UpdatePersonMultiUI();
        _ = LoadImgPageAsync();
        ShowNotice($"已删除 {ok} 个人物");
    }

    private void PersonMultiCancel_Click(object sender, RoutedEventArgs e)
    {
        _personMulti = false;
        UpdatePersonMultiUI();
    }

    /// <summary>当前人物列表绑定的数据项</summary>
    private IEnumerable<PersonItem> CurrentPersonItems()
        => PersonGrid.ItemsSource is IEnumerable<PersonItem> list ? list : Enumerable.Empty<PersonItem>();

    private void TagItem_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is Border b && b.DataContext is TagItem t)
        {
            _imgMode = "tagPhotos";
            _currentTagId = t.Id;
            _currentTagName = t.Name;
            _tagPage = 0;
            ImgBackBtn.Visibility = Visibility.Visible;
            TagNameText.Text = $"# {t.Name}（{t.Count}）";
            TagActionBar.Visibility = Visibility.Visible;
            TagAndBtn.Visibility = Visibility.Visible;
            TagClearAndBtn.Visibility = _imgTagIds.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ApplyFilterBarVisibility(forceCollapsed: true);
            _ = LoadImgPageAsync();
        }
    }

    private string _currentTagName = "";

    /// <summary>把当前标签相册的标签并入“全部”页 AND 组合（叠加取交集），顶部以可移除芯片展示。</summary>
    private void TagAnd_Click(object sender, RoutedEventArgs e)
    {
        if (_currentTagId > 0 && !_imgTagIds.Contains(_currentTagId))
        {
            _imgTagIds.Add(_currentTagId);
            _imgTagChips.Add((_currentTagId, _currentTagName));
        }
        ExitTagPhotosToAll();
    }

    private void TagClearAnd_Click(object sender, RoutedEventArgs e)
    {
        _imgTagIds.Clear();
        _imgTagChips.Clear();
        ExitTagPhotosToAll();
    }

    private void ExitTagPhotosToAll()
    {
        _imgMode = "all";
        _imgPage = 0;
        ImgTabAll.IsChecked = true;
        TagActionBar.Visibility = Visibility.Collapsed;
        ImgBackBtn.Visibility = Visibility.Collapsed;
        SetFilterBarVisibility();
        _ = LoadImgPageAsync();
    }

    /// <summary>渲染已选标签芯片（每个可点 × 移除，移除后自动重载交集）。</summary>
    private void RenderTagChips()
    {
        if (ImgTagChipPanel == null) return;
        ImgTagChipPanel.Children.Clear();
        var show = _imgMode == "all" && _imgTagChips.Count > 0;
        ImgTagChipPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        foreach (var (id, name) in _imgTagChips)
        {
            var chip = new Border
            {
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x24, 0x33, 0x58)),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(8, 3, 6, 3),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock
            {
                Text = "# " + name,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x7F, 0xA8, 0xFF)),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center
            });
            var x = new Button
            {
                Content = "×",
                Tag = id,
                Width = 16, Height = 16,
                Padding = new Thickness(0),
                Margin = new Thickness(5, 0, 0, 0),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC8, 0xD0, 0xE2)),
                Cursor = AppCursors.Hand
            };
            x.Click += RemoveTagChip_Click;
            row.Children.Add(x);
            chip.Child = row;
            ImgTagChipPanel.Children.Add(chip);
        }
    }

    private void RemoveTagChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is long id)
        {
            _imgTagIds.Remove(id);
            _imgTagChips.RemoveAll(c => c.Id == id);
            _imgPage = 0;
            RenderTagChips();
            _ = LoadImgPageAsync();
        }
    }

    // ==================== 空态三件套（0.46.0：主句 + 副句 + 行动引导） ====================

    private Action? _imgEmptyAction;
    private Action? _promptEmptyAction;

    /// <summary>显示图片库空态：主句 / 副句 / 可选行动按钮（action 为 null 时隐藏按钮）。</summary>
    private void ShowImgEmpty(string title, string hint, string? actionText, Action? action)
    {
        if (ImgEmptyBox == null) return;
        ImgEmptyTitle.Text = title;
        ImgEmptyHint.Text = hint;
        _imgEmptyAction = action;
        if (action != null && actionText != null)
        {
            ImgEmptyAction.Content = actionText;
            ImgEmptyAction.Visibility = Visibility.Visible;
        }
        else ImgEmptyAction.Visibility = Visibility.Collapsed;
        ImgEmptyBox.Visibility = Visibility.Visible;
    }

    private void HideImgEmpty()
    {
        if (ImgEmptyBox == null) return;
        ImgEmptyBox.Visibility = Visibility.Collapsed;
        _imgEmptyAction = null;
    }

    private void ImgEmptyAction_Click(object sender, RoutedEventArgs e) => _imgEmptyAction?.Invoke();

    /// <summary>显示提示词空态：主句 / 副句 / 可选行动按钮。</summary>
    private void ShowPromptEmpty(string title, string hint, string? actionText, Action? action)
    {
        if (PromptEmptyBox == null) return;
        PromptEmptyTitle.Text = title;
        PromptEmptyHint.Text = hint;
        _promptEmptyAction = action;
        if (action != null && actionText != null)
        {
            PromptEmptyAction.Content = actionText;
            PromptEmptyAction.Visibility = Visibility.Visible;
        }
        else PromptEmptyAction.Visibility = Visibility.Collapsed;
        PromptEmptyBox.Visibility = Visibility.Visible;
    }

    private void HidePromptEmpty()
    {
        if (PromptEmptyBox == null) return;
        PromptEmptyBox.Visibility = Visibility.Collapsed;
        _promptEmptyAction = null;
    }

    private void PromptEmptyAction_Click(object sender, RoutedEventArgs e) => _promptEmptyAction?.Invoke();

    /// <summary>图片库是否处于「非默认」筛选（状态 / 排序 / 来源 / 关键词 / 标签 AND 任一激活）。</summary>
    private bool HasActiveImgFilter()
        => _imgStatus != "all"
           || _imgSort != "date_desc"
           || !string.IsNullOrEmpty(_imgFolder)
           || !string.IsNullOrEmpty(ImgSearchBox?.Text?.Trim())
           || _imgTagIds.Count > 0;

    /// <summary>一键清除图片库全部筛选，回到默认「全部 / 时间↓ / 全部来源 / 空关键词」。</summary>
    private void ClearImgFilters()
    {
        if (!_imgFilterReady) return;
        _imgFilterReady = false;
        _imgStatus = "all";
        _imgSort = "date_desc";
        _imgFolder = "";
        _imgPage = 0;
        _imgTagIds.Clear();
        _imgTagChips.Clear();
        if (ImgSearchBox != null) ImgSearchBox.Text = "";
        if (ImgStatusAllChip != null) ImgStatusAllChip.IsChecked = true;
        if (ImgSortDateChip != null) ImgSortDateChip.IsChecked = true;
        UpdateFolderChips();
        _imgFilterReady = true;
        RenderTagChips();
        _ = LoadImgPageAsync();
    }

    /// <summary>空态引导：跳到设置页添加图片文件夹。</summary>
    private void GoToAddFolder() => NavSettings.IsChecked = true;

    /// <summary>按状态给状态文本着色：true=成功(绿) / false=异常(红) / null=中性(灰)。</summary>
    private static void TintStatus(System.Windows.Controls.TextBlock? tb, bool? ok)
    {
        if (tb == null) return;
        var key = ok == true ? "SuccessBrush" : ok == false ? "DangerBrush" : "TextDimBrush";
        tb.Foreground = (System.Windows.Media.Brush)Application.Current.FindResource(key);
    }

    // ==================== 幽灵卡片清理（外部删除文件后） ====================

    private void CleanupGhostCards()
    {
        try
        {
            if (!ImgGrid.IsVisible) return;
            if (_imgLoading) return; // 加载进行中不清理，避免边加载边删导致闪烁，下个周期再清
            if (ImgGrid.ItemsSource is not System.Collections.IList items || items.Count == 0) return;

            List<ImgItem>? gone = null;
            foreach (ImgItem it in items)
            {
                // 必须用 IsMissing（含盘符可用性判断）：定时器周期执行，盘掉线时不能误删整页（0.62.3）
                if (ImageLibraryService.IsMissing(it.Path))
                {
                    (gone ??= new List<ImgItem>()).Add(it);
                }
            }
            if (gone == null || gone.Count == 0) return;

            // 清理索引与识别库记录
            foreach (var g in gone)
            {
                try { _img.RemoveIndexEntry(g.Path); _img.RemoveImgTagEntry(g.Path); } catch { }
            }
            // 从页面移除占位。
            // 同样要用 IsMissing：若盘掉线就用 File.Exists 过滤，当前页卡片会被整页清空，
            // 看起来就像"图片全消失了"（0.62.3）——盘不可用时应保留卡片，而不是判死刑。
            var remaining = items.Cast<ImgItem>().Where(it => !ImageLibraryService.IsMissing(it.Path)).ToList();
            ImgGrid.ItemsSource = remaining;
            try { ImgCountText.Text = $"共 {_img.Count()} 张"; } catch { }
        }
        catch { }
    }

    /// <summary>图片库搜索防抖：连续输入只在停顿 300ms 后查询一次</summary>
    private void ScheduleImgSearch()
    {
        _imgSearchCts?.Cancel();
        var cts = _imgSearchCts = new CancellationTokenSource();
        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(300, token); }
            catch (OperationCanceledException) { return; }
            if (token.IsCancellationRequested) return;
            await Dispatcher.InvokeAsync(() => { _imgPage = 0; _ = LoadImgPageAsync(); });
        });
    }

    /// <summary>标签搜索防抖：连续输入只在停顿 200ms 后重新加载标签</summary>
    private void ScheduleTagSearch()
    {
        _tagSearchCts?.Cancel();
        var cts = _tagSearchCts = new CancellationTokenSource();
        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(200, token); }
            catch (OperationCanceledException) { return; }
            if (token.IsCancellationRequested) return;
            await Dispatcher.InvokeAsync(() => _ = LoadImgPageAsync());
        });
    }

    /// <summary>提示词列表过滤防抖（250ms）</summary>
    private void SchedulePromptFilter()    {
        _promptSearchCts?.Cancel();
        var cts = _promptSearchCts = new CancellationTokenSource();
        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(250, token); }
            catch (OperationCanceledException) { return; }
            if (token.IsCancellationRequested) return;
            await Dispatcher.InvokeAsync(ApplyFilter);
        });
    }

    /// <summary>次版本号转中文：0..9 零至九，10 十，11 十九，20 二十，21 二十一……</summary>
    private static string CnVersionName(int n)
    {
        string[] digit = { "零", "一", "二", "三", "四", "五", "六", "七", "八", "九" };
        if (n < 10) return digit[n];
        if (n == 10) return "十";
        if (n < 20) return "十" + digit[n - 10];
        int tens = n / 10, ones = n % 10;
        var s = digit[tens] + "十";
        if (ones > 0) s += digit[ones];
        return s;
    }

    /// <summary>小内存解码本地图片并冻结（可跨线程），失败返回 null</summary>
    private static BitmapImage? DecodeImageFile(string path, int decodeWidth)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path);
            bmp.DecodePixelWidth = decodeWidth;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    /// <summary>从字节解码图片并冻结（可跨线程），失败返回 null</summary>
    private static BitmapImage? DecodeImageBytes(byte[] bytes, int decodeWidth)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = ms;
            bmp.DecodePixelWidth = decodeWidth;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    /// <summary>
    /// 本地 .faces 缓存缺失的人物封面，向 imgtag 服务拉取人脸裁剪图（服务端会回写缓存），
    /// 拉到后就地刷新卡片；不阻塞列表显示。
    /// </summary>
    private async Task BackfillPersonAvatarsAsync(List<PersonItem> persons, CancellationToken token = default)
    {
        try
        {
            var missing = persons.Where(p => p.AvatarFaceId.HasValue &&
                !File.Exists(Path.Combine(_imgTagDir, ".faces", p.AvatarFaceId.Value + ".jpg"))).ToList();
            if (missing.Count == 0) return;
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            using var sem = new SemaphoreSlim(6);
            await Task.WhenAll(missing.Select(async p =>
            {
                await sem.WaitAsync();
                try
                {
                    token.ThrowIfCancellationRequested();
                    var url = $"{ImgtagRecognizer.ServerUrl}/api/face-thumbnail/{p.AvatarFaceId}";
                    var bytes = await http.GetByteArrayAsync(url, token);
                    var img = DecodeImageBytes(bytes, 160);
                    if (img != null && !token.IsCancellationRequested)
                        await Dispatcher.InvokeAsync(() => { if (!token.IsCancellationRequested) p.Thumb = img; });
                }
                catch { }
                finally { sem.Release(); }
            }));
        }
        catch { }
    }

    private async Task LoadImgPageAsync()
    {
        var stack = new System.Diagnostics.StackTrace();
        LogDebug($"LoadImgPageAsync called. mode={_imgMode}\n{stack}");
        // 单一飞行结果：任何新加载都让上一次失效，过期结果不上屏
        _imgListCts?.Cancel();
        _imgListCts?.Dispose();
        var cts = _imgListCts = new CancellationTokenSource();
        var token = cts.Token;
        _imgLoading = true;
        ImgLoadingMask.Visibility = Visibility.Visible;
        try
        {
            var page = _imgPage;
            var mode = _imgMode;
            var search = ImgSearchBox.Text.Trim();

            // 人物/标签列表：只取轻量元数据立即上壳，封面/图片异步渐进填充
            if (mode == "persons" || mode == "tags")
            {
                var persons = mode == "persons" ? await Task.Run(() => _img.Persons()) : new List<PersonItem>();
                token.ThrowIfCancellationRequested();

                List<TagItem> tags = new();
                int tagTotal = 0;
                if (mode == "tags")
                {
                    var kw = TagSearchBox.Text.Trim();
                    (tags, tagTotal) = await Task.Run(() => (_img.Tags(kw, 200), _img.TagTotal()));
                }
                token.ThrowIfCancellationRequested();

                await Dispatcher.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested) return;
                    PersonGrid.Visibility = mode == "persons" ? Visibility.Visible : Visibility.Collapsed;
                    TagGrid.Visibility = mode == "tags" ? Visibility.Visible : Visibility.Collapsed;
                    ImgGrid.Visibility = Visibility.Collapsed;
                    PersonGrid.ItemsSource = persons;
                    TagGrid.ItemsSource = tags;
                    LogDebug($"LoadImgPage: set PersonGrid.ItemsSource count={persons.Count} _personMulti={_personMulti} PersonMultiVisible={PersonMultiVisible}");
                    // 如果处于人物多选模式，给新加载的项同步 MultiMode
                    if (_personMulti && mode == "persons")
                    {
                        foreach (var p in persons) p.MultiMode = true;
                    }
                    if (mode == "persons" && persons.Count == 0)
                        ShowImgEmpty("还没有人物", "识别图片中的人脸后，人物会自动整理到这里", null, null);
                    else if (mode == "tags" && tags.Count == 0)
                        ShowImgEmpty(string.IsNullOrEmpty(TagSearchBox.Text.Trim()) ? "还没有标签" : "没有匹配的标签", "AI 打标后，图片标签会汇总到这里", null, null);
                    else HideImgEmpty();
                    ImgLoadingMask.Visibility = Visibility.Collapsed;
                    if (mode == "persons") ImgCountText.Text = $"人物 {persons.Count}";
                    else ImgCountText.Text = string.IsNullOrEmpty(TagSearchBox.Text.Trim())
                        ? $"标签 {tags.Count} / 共 {tagTotal}" : $"标签 {tags.Count}";
                    ImgPageText.Text = "";
                    ImgPrevBtn.IsEnabled = false;
                    ImgNextBtn.IsEnabled = false;
                    if (ImgPageBar != null) ImgPageBar.Items.Clear();
                });
                if (mode == "persons" && !token.IsCancellationRequested) _ = FillPersonThumbsAsync(persons, token);
                return;
            }

            // 图片网格模式（全部/人物相册/标签相册）：只查元数据，不解码缩略图
            EnsurePromptImageFileSet();
            var fq0 = BuildImgQuery(); // 必须在 UI 线程构造（读取搜索框等控件）
            var items = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                List<ImgItem> rows;
                int total;
                switch (mode)
                {
                    case "personPhotos":
                        total = _img.PersonPhotoCount(_currentPersonId);
                        rows = _img.PersonPhotos(_currentPersonId, page * ImgPageSize, ImgPageSize);
                        break;
                    case "tagPhotos":
                        total = _img.TagPhotoCount(_currentTagId);
                        rows = _img.TagPhotos(_currentTagId, page * ImgPageSize, ImgPageSize);
                        break;
                    case "idPhotos":
                        total = _img.CountByTagKeywords(ImageLibraryService.IdPhotoKeywords);
                        rows = _img.PhotosByTagKeywords(ImageLibraryService.IdPhotoKeywords, page * ImgPageSize, ImgPageSize);
                        break;
                    case "docPhotos":
                        total = _img.CountByTagKeywords(ImageLibraryService.DocKeywords);
                        rows = _img.PhotosByTagKeywords(ImageLibraryService.DocKeywords, page * ImgPageSize, ImgPageSize);
                        break;
                    default:
                        // 阶段3：关键词(FTS/LIKE) + 排序 + 状态 + 来源 + 标签 AND，全部在 SQL 下推，保证满页与总数一致
                        total = _img.QueryCount(fq0);
                        rows = _img.QueryPage(fq0, page * ImgPageSize, ImgPageSize);
                        break;
                }
                // 过滤已被删除的文件，避免占位卡片；顺带清理索引与识别库。
                // 必须用 IsMissing（含盘符可用性判断）：盘一掉线不能把整页照片当失效清掉（0.62.3）
                var gone = rows.Where(it => ImageLibraryService.IsMissing(it.Path)).ToList();
                if (gone.Count > 0)
                {
                    foreach (var g in gone)
                    {
                        try { _img.RemoveIndexEntry(g.Path); _img.RemoveImgTagEntry(g.Path); } catch { }
                    }
                }
                // 只过滤「确认缺失」的（含盘符可用性判断）。
                // 用 File.Exists 的话，盘一掉线整页卡片就没有了 —— 这正是"图片全消失"的观感来源（0.62.3）
                rows = rows.Where(it => !ImageLibraryService.IsMissing(it.Path)).ToList();
                foreach (var it in rows) it.HasPrompt = _promptImageFiles.Contains(it.Filename);
                return (rows, total);
            });
            token.ThrowIfCancellationRequested();

            await Dispatcher.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested) return;
                _imgSel.Clear(); // 新一页数据，旧页选中不再适用
                _lastImgFocus = null;
                ImgGrid.Visibility = Visibility.Visible;
                PersonGrid.Visibility = Visibility.Collapsed;
                TagGrid.Visibility = Visibility.Collapsed;
                ImgGrid.ItemsSource = items.rows;
                if (items.rows.Count > 0)
                {
                    HideImgEmpty();
                }
                else if (items.total > 0)
                {
                    ShowImgEmpty("本页没有更多图片", "回到上一页继续浏览", null, null);
                }
                else if (_imgMode == "personPhotos")
                {
                    ShowImgEmpty("这个人物还没有照片", "识别出的该人物照片会出现在这里", null, null);
                }
                else if (_imgMode == "tagPhotos")
                {
                    ShowImgEmpty("这个标签下还没有图片", "给图片打上该标签后会出现在这里", null, null);
                }
                else if (_imgMode == "idPhotos")
                {
                    ShowImgEmpty("还没有证件照", "AI 打标后，证件类图片会归到这里", null, null);
                }
                else if (_imgMode == "docPhotos")
                {
                    ShowImgEmpty("还没有文档", "AI 打标后，文档类图片会归到这里", null, null);
                }
                else if (HasActiveImgFilter())
                {
                    ShowImgEmpty("没有符合条件的图片", "当前的筛选或搜索太严格了，清掉它们再试试", "清除筛选", ClearImgFilters);
                }
                else if (_settings.ImageFolders.Count == 0)
                {
                    ShowImgEmpty("图片库还是空的", "先在设置里添加图片文件夹，再扫描入库", "去设置添加文件夹", GoToAddFolder);
                }
                else
                {
                    ShowImgEmpty("还没有图片", "文件夹已就绪，点「设置 → 立即扫描」把图片入库", null, null);
                }
                ImgLoadingMask.Visibility = Visibility.Collapsed;
                ImgCountText.Text = $"共 {items.total} 张";
                var pageCount = Math.Max(1, (items.total + ImgPageSize - 1) / ImgPageSize);
                ImgPageText.Text = $"{page + 1} / {pageCount}";
                ImgPrevBtn.IsEnabled = page > 0;
                ImgNextBtn.IsEnabled = page + 1 < pageCount;
                RenderImgPageBar(page, pageCount);
            });
            // 缩略图：磁盘缓存优先、限并发、随加载取消，经 INPC 渐进回填（先壳后图）
            _ = FillImgThumbsAsync(items.rows, token);
        }
        catch (OperationCanceledException) { /* 过期加载，静默丢弃 */ }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() => ImgCountText.Text = "加载失败: " + ex.Message);
        }
        finally
        {
            _imgLoading = false;
            ImgLoadingMask.Visibility = Visibility.Collapsed;
            DumpImgScrollDiag();
            // 自检：确认遮罩不参与命中（若 IsHitTestVisible=False 生效，此处应命中卡片而非遮罩）
            try
            {
                var svPt = ImgListScroll.TransformToAncestor(this).Transform(new System.Windows.Point(20, 20));
                Diag2($"SELFCHECK mask.IHtV={ImgLoadingMask?.IsHitTestVisible} vis={ImgLoadingMask?.Visibility} hit={HitChainOf(this, svPt)}");
            }
            catch { }
        }
    }

    /// <summary>把图片库滚动度量写入临时日志，便于定位“滚不动”是 extent=0 还是命中测试问题。</summary>
    /// <summary>诊断日志：鼠标/滚轮/勾选事件链路（%TEMP%\pb_diag2.txt，问题定位用）</summary>
    private static void Diag2(string msg)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pb_diag2.txt"),
                $"{DateTime.Now:HH:mm:ss.fff} {msg}\n");
        }
        catch { }
    }

    /// <summary>命中链上是否存在带 ContextMenu 的元素（等价 WPF 默认右键语义）。</summary>
    private static bool HitHasContextMenu(System.Windows.Media.Visual root, System.Windows.Point pt)
    {
        bool found = false;
        System.Windows.Media.VisualTreeHelper.HitTest(root, null,
            hr =>
            {
                System.Windows.DependencyObject? d = hr.VisualHit;
                while (d != null)
                {
                    if (d is FrameworkElement fe && fe.ContextMenu != null) { found = true; break; }
                    d = System.Windows.Media.VisualTreeHelper.GetParent(d);
                }
                return System.Windows.Media.HitTestResultBehavior.Stop;
            },
            new System.Windows.Media.PointHitTestParameters(pt));
        return found;
    }

    /// <summary>兜底打开卡片的右键菜单（取卡片自己的 ContextMenu 实例，DataContext 天然指向该卡片项）。</summary>
    private static bool OpenCardContextMenu(ItemsControl grid, object item)
    {
        if (grid.ItemContainerGenerator.ContainerFromItem(item) is not System.Windows.DependencyObject container) return false;
        var border = FindVisualChild<Border>(container);
        var menu = border?.ContextMenu;
        if (border == null || menu == null) return false;
        menu.PlacementTarget = border;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
        return true;
    }

    // ---------------- 动效（计划 §2.4：只动 opacity，120–240ms；跟随系统「减弱动画」） ----------------

    /// <summary>淡入（只动 opacity）。系统开启「减弱动画」时直接显示、不做动画。</summary>
    private static void FadeIn(FrameworkElement? el, double ms = 180)
    {
        if (el == null) return;
        if (!System.Windows.SystemParameters.ClientAreaAnimation)
        {
            el.BeginAnimation(OpacityProperty, null);
            el.Opacity = 1;
            return;
        }
        el.Opacity = 0;
        el.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)));
    }

    /// <summary>淡出后折叠（同样只动 opacity）；「减弱动画」时直接折叠。</summary>
    private static void FadeOutAndCollapse(FrameworkElement? el, double ms = 180, Action? after = null)
    {
        if (el == null) { after?.Invoke(); return; }
        if (!System.Windows.SystemParameters.ClientAreaAnimation)
        {
            el.BeginAnimation(OpacityProperty, null);
            el.Opacity = 1;
            el.Visibility = Visibility.Collapsed;
            after?.Invoke();
            return;
        }
        var anim = new System.Windows.Media.Animation.DoubleAnimation(el.Opacity, 0, TimeSpan.FromMilliseconds(ms));
        anim.Completed += (_, _) =>
        {
            el.BeginAnimation(OpacityProperty, null);   // 清掉动画并还原，避免污染后续显示
            el.Opacity = 1;
            el.Visibility = Visibility.Collapsed;
            after?.Invoke();
        };
        el.BeginAnimation(OpacityProperty, anim);
    }

    /// <summary>
    /// 在网格中按坐标找条目（兜底点击用）。先常规命中测试、沿父链找 DataContext 为 T 的元素；
    /// 失败再按容器矩形包含判定——这样即使命中的是滚动背景、卡片间隙，或最近的 Border 不是卡片本身，
    /// 也能正确识别出被点的条目（此前用「最近 Border 祖先」判定，非「全部」时经常落空导致点不开）。
    /// </summary>
    private static T? ItemAtPoint<T>(System.Windows.Controls.ItemsControl grid, System.Windows.Point pt) where T : class
    {
        try
        {
            var hit = System.Windows.Media.VisualTreeHelper.HitTest(grid, pt)?.VisualHit as DependencyObject;
            while (hit != null)
            {
                if (hit is FrameworkElement fe && fe.DataContext is T t) return t;
                hit = System.Windows.Media.VisualTreeHelper.GetParent(hit);
            }
        }
        catch { }

        // 兜底：按容器矩形判定（卡片间隙、命中错位都能救回来）
        try
        {
            for (int i = 0; i < grid.Items.Count; i++)
            {
                if (grid.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement c) continue;
                if (c.Visibility != Visibility.Visible || c.ActualWidth <= 0) continue;
                var p = c.TranslatePoint(new System.Windows.Point(0, 0), grid);
                if (pt.X >= p.X && pt.Y >= p.Y && pt.X <= p.X + c.ActualWidth && pt.Y <= p.Y + c.ActualHeight)
                    return c.DataContext as T;
            }
        }
        catch { }
        return null;
    }

    /// <summary>WPF 命中测试：返回鼠标位置命中的视觉元素链（用于定位透明拦截层/命中错位）</summary>
    private static string HitChainOf(UIElement root, System.Windows.Point pt)
    {
        try
        {
            var hit = System.Windows.Media.VisualTreeHelper.HitTest(root, pt)?.VisualHit as DependencyObject;
            if (hit == null) return "<none>";
            var parts = new System.Collections.Generic.List<string>();
            var d = hit;
            while (d != null)
            {
                var fe = d as FrameworkElement;
                parts.Add(fe != null && !string.IsNullOrEmpty(fe.Name) ? $"{fe.Name}:{d.GetType().Name}" : d.GetType().Name);
                d = System.Windows.Media.VisualTreeHelper.GetParent(d);
            }
            return string.Join("<", parts);
        }
        catch { return "<err>"; }
    }

    /// <summary>输入命中测试（真实鼠标语义）：尊重 IsHitTestVisible/Visibility，返回鼠标真正会命中的元素链</summary>
    private static string InputHitOf(UIElement root, System.Windows.Point pt)
    {
        try
        {
            var hit = root.InputHitTest(pt) as DependencyObject;
            if (hit == null) return "<null>";
            var parts = new System.Collections.Generic.List<string>();
            var d = hit;
            while (d != null)
            {
                var fe = d as FrameworkElement;
                parts.Add(fe != null && !string.IsNullOrEmpty(fe.Name) ? $"{fe.Name}:{d.GetType().Name}" : d.GetType().Name);
                d = System.Windows.Media.VisualTreeHelper.GetParent(d);
            }
            return string.Join("<", parts);
        }
        catch { return "<err>"; }
    }

    private void DumpImgScrollDiag()
    {        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    var sv = ImgListScroll;
                    var grid = ImgGrid;
                    var view = ImgView;
                    int n = grid?.Items?.Count ?? -1;
                    int nPerson = PersonGrid?.Items?.Count ?? -1;
                    string line =
                        $"{DateTime.Now:HH:mm:ss.fff} mode={_imgMode} " +
                        $"ImgView={view?.ActualHeight:F0}x{view?.ActualWidth:F0} " +
                        $"SV={sv?.ActualHeight:F0}x{sv?.ActualWidth:F0} " +
                        $"VP={sv?.ViewportHeight:F0} EX={sv?.ExtentHeight:F0} SH={sv?.ScrollableHeight:F0} off={sv?.VerticalOffset:F0} " +
                        $"ImgGridH={grid?.ActualHeight:F0} items={n} persons={nPerson} " +
                        $"status={_imgStatus} folder='{_imgFolder}' kw='{ImgSearchBox?.Text}' count='{ImgCountText?.Text}' " +
                        $"mask={(ImgLoadingMask?.Visibility.ToString() ?? "?")}\n";
                    var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pb_scroll_diag.txt");
                    System.IO.File.AppendAllText(path, line);
                }
                catch { }
            }, System.Windows.Threading.DispatcherPriority.Background);
        }
        catch { }
    }

    /// <summary>图片网格缩略图渐进填充：命中 ThumbCache 近瞬出图，未命中解码原图并回写缓存，可取消。</summary>
    private async Task FillImgThumbsAsync(List<ImgItem> rows, CancellationToken token)
    {
        using var sem = new SemaphoreSlim(4);
        await Task.WhenAll(rows.Select(async it =>
        {
            await sem.WaitAsync(token);
            try
            {
                var path = it.Path;
                var bmp = await Task.Run(() => ThumbCache.GetOrCreate(path, 300, out _), token);
                if (bmp != null && !token.IsCancellationRequested)
                    await Dispatcher.InvokeAsync(() => { if (!token.IsCancellationRequested) it.Thumb = bmp; },
                        System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (OperationCanceledException) { }
            catch { }
            finally { try { sem.Release(); } catch { } }
        }));
    }

    /// <summary>人物封面渐进填充：先本地 .faces/首张原图，缺失再由 Backfill 走 HTTP 补齐。</summary>
    private async Task FillPersonThumbsAsync(List<PersonItem> persons, CancellationToken token)
    {
        try
        {
            using var sem = new SemaphoreSlim(6);
            await Task.WhenAll(persons.Select(async p =>
            {
                await sem.WaitAsync(token);
                try
                {
                    var avatarId = p.AvatarFaceId;
                    var sample = p.SamplePath;
                    var tagDir = _imgTagDir;
                    var bmp = await Task.Run(() =>
                    {
                        string? faceFile = avatarId.HasValue ? Path.Combine(tagDir, ".faces", avatarId.Value + ".jpg") : null;
                        if (faceFile != null && File.Exists(faceFile)) return ThumbCache.Decode(faceFile, 160);
                        if (!string.IsNullOrEmpty(sample) && File.Exists(sample)) return ThumbCache.Decode(sample, 160);
                        return null;
                    }, token);
                    if (bmp != null && !token.IsCancellationRequested)
                        await Dispatcher.InvokeAsync(() => { if (!token.IsCancellationRequested) p.Thumb = bmp; },
                            System.Windows.Threading.DispatcherPriority.Background);
                }
                catch (OperationCanceledException) { }
                catch { }
                finally { try { sem.Release(); } catch { } }
            }));
            if (!token.IsCancellationRequested) await BackfillPersonAvatarsAsync(persons, token);
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private async void ImgPrev_Click(object sender, RoutedEventArgs e)
    {
        if (_imgPage <= 0) return;
        _imgPage--;
        await LoadImgPageAsync();
    }

    private async void ImgNext_Click(object sender, RoutedEventArgs e)
    {
        _imgPage++;
        await LoadImgPageAsync();
    }

    /// <summary>
    /// 渲染图片库页码按钮（点击直达）。页数 ≤9 全列；更多时列首尾 + 当前页附近，中间以省略号连接。
    /// 只操作分页栏，不涉及图片网格与滚动逻辑。
    /// </summary>
    private void RenderImgPageBar(int current, int pageCount)
    {
        if (ImgPageBar == null) return;
        ImgPageBar.Items.Clear();
        if (pageCount <= 1) return;

        var pages = new List<int>();
        if (pageCount <= 9)
        {
            for (int i = 0; i < pageCount; i++) pages.Add(i);
        }
        else
        {
            int s = Math.Max(0, current - 3);
            int e = Math.Min(pageCount - 1, current + 3);
            if (s > 0) { pages.Add(0); if (s > 1) pages.Add(-1); }
            for (int i = s; i <= e; i++) pages.Add(i);
            if (e < pageCount - 1) { if (e < pageCount - 2) pages.Add(-1); pages.Add(pageCount - 1); }
        }

        var faint = (System.Windows.Media.Brush)FindResource("TextFaintBrush");
        var style = (Style)FindResource("ActionBtn");
        foreach (var p in pages)
        {
            if (p < 0)
            {
                ImgPageBar.Items.Add(new System.Windows.Controls.TextBlock
                {
                    Text = "…",
                    Foreground = faint,
                    FontSize = (double)FindResource("TypeBody"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new System.Windows.Thickness(2, 0, 2, 0)
                });
                continue;
            }
            var btn = new System.Windows.Controls.Button
            {
                Content = (p + 1).ToString(),
                Tag = p,
                Style = style,
                MinWidth = 30,
                Height = 26,
                Padding = new System.Windows.Thickness(6, 0, 6, 0),
                Margin = new System.Windows.Thickness(2, 0, 2, 0),
                ToolTip = $"第 {p + 1} 页"
            };
            btn.Click += ImgPageBtn_Click;
            if (p == current)
            {
                // 当前页：强调底 + 蓝描边。刻意不用 BtnPrimary，避免同屏出现第二个主按钮
                btn.Background = (System.Windows.Media.Brush)FindResource("AccentSoftBrush");
                btn.BorderBrush = (System.Windows.Media.Brush)FindResource("AccentBrush");
                btn.FontWeight = System.Windows.FontWeights.SemiBold;
            }
            ImgPageBar.Items.Add(btn);
        }
    }

    /// <summary>点击页码直达（翻页与「上一页/下一页」走同一入口）</summary>
    private async void ImgPageBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button b && b.Tag is int p)
        {
            if (p == _imgPage) return;
            _imgPage = p;
            await LoadImgPageAsync();
        }
    }

    /// <summary>构造“全部”网格当前的筛选/排序查询快照（列表与查看器序列共用同一口径）。</summary>
    private ImageLibraryService.ImgQuery BuildImgQuery() => new()
    {
        Keyword = ImgSearchBox.Text.Trim(),
        Sort = _imgSort,
        Status = _imgStatus,
        Folder = string.IsNullOrEmpty(_imgFolder) ? null : _imgFolder,
        TagIds = _imgTagIds.ToList(),
        PromptFiles = _promptImageFiles
    };

    /// <summary>查看器连续导航序列：证件照 / 文档这类「标签族关键词」页专用（0.62.4）。</summary>
    private sealed class ImgKwSeq : ImageLibraryService.IImgSequence
    {
        private readonly ImageLibraryService _lib;
        private readonly string[] _kw;
        public int Total { get; }
        public ImgKwSeq(ImageLibraryService lib, string[] kw, int total) { _lib = lib; _kw = kw; Total = total; }
        public int RankOf(string path) => _lib.KeywordRank(_kw, path);
        public ImgItem? At(int zeroBasedRank)
        {
            if (zeroBasedRank < 0 || zeroBasedRank >= Total) return null;
            var l = _lib.PhotosByTagKeywords(_kw, zeroBasedRank, 1);
            return l.Count > 0 ? l[0] : null;
        }
    }

    /// <summary>查看器连续导航序列：人物相册（personPhotos）专用。</summary>
    private sealed class ImgPersonSeq : ImageLibraryService.IImgSequence
    {
        private readonly ImageLibraryService _lib;
        private readonly long _personId;
        public int Total { get; }
        public ImgPersonSeq(ImageLibraryService lib, long personId, int total) { _lib = lib; _personId = personId; Total = total; }
        public int RankOf(string path) => _lib.PersonPhotoRank(_personId, path);
        public ImgItem? At(int zeroBasedRank)
        {
            if (zeroBasedRank < 0 || zeroBasedRank >= Total) return null;
            var l = _lib.PersonPhotos(_personId, zeroBasedRank, 1);
            return l.Count > 0 ? l[0] : null;
        }
    }

    /// <summary>查看器连续导航序列：标签相册（tagPhotos）专用。</summary>
    private sealed class ImgTagSeq : ImageLibraryService.IImgSequence
    {
        private readonly ImageLibraryService _lib;
        private readonly long _tagId;
        public int Total { get; }
        public ImgTagSeq(ImageLibraryService lib, long tagId, int total) { _lib = lib; _tagId = tagId; Total = total; }
        public int RankOf(string path) => _lib.TagPhotoRank(_tagId, path);
        public ImgItem? At(int zeroBasedRank)
        {
            if (zeroBasedRank < 0 || zeroBasedRank >= Total) return null;
            var l = _lib.TagPhotos(_tagId, zeroBasedRank, 1);
            return l.Count > 0 ? l[0] : null;
        }
    }

    /// <summary>查看器连续导航序列：对当前筛选结果按全局序号随机访问。</summary>
    private sealed class ImgSeq : ImageLibraryService.IImgSequence
    {
        private readonly ImageLibraryService _lib;
        private readonly ImageLibraryService.ImgQuery _q;
        public int Total { get; }
        public ImgSeq(ImageLibraryService lib, ImageLibraryService.ImgQuery q, int total) { _lib = lib; _q = q; Total = total; }
        public int RankOf(string path) => _lib.QueryRank(_q, path);
        public ImgItem? At(int zeroBasedRank)
        {
            if (zeroBasedRank < 0 || zeroBasedRank >= Total) return null;
            var l = _lib.QueryPage(_q, zeroBasedRank, 1);
            return l.Count > 0 ? l[0] : null;
        }
    }

    private async void ImgItem_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        Diag2($"CLICK-BORDER multi={_imgMulti}");
        if (sender is Border b && b.DataContext is ImgItem item)
        {
            b.Focus(); // 卡片可聚焦，保证后续键盘事件（Enter/Space 等）经 ScrollViewer 路由到 ImgGrid_KeyDown
            _lastImgFocus = item;
            if (_imgMulti)
            {
                if (FindAncestor<CheckBox>(e.OriginalSource as DependencyObject) != null) return;
                item.Selected = !item.Selected;
                if (item.Selected) _imgSel.Add(item.Path); else _imgSel.Remove(item.Path);
                UpdateImgSelCount();
                e.Handled = true;
                return;
            }
            await OpenImgViewerAsync(item);
            e.Handled = true;
        }
    }

    /// <summary>
    /// 打开图片查看器。要点：**先无条件把窗口显示出来**，导航序列随后异步附加——
    /// 这样任何数据库调用都不会挡住「点开图片」（历史问题：点击后无反应、无报错、无日志）。
    /// </summary>
    private Task OpenImgViewerAsync(ImgItem item)
    {
        try
        {
            if (item == null) return Task.CompletedTask;
            Diag2($"OPEN enter mode={_imgMode} status={_imgStatus} exists={(!string.IsNullOrEmpty(item.Path) && File.Exists(item.Path))} path='{item.Path}'");
            // 盘不可用（移动盘/网络盘掉线）时不算"文件不存在"：既不能打开，也不能清记录（0.62.3）
            if (!string.IsNullOrEmpty(item.Path) && !File.Exists(item.Path) && !ImageLibraryService.IsMissing(item.Path))
            {
                Diag2($"OPEN drive unavailable '{item.Path}'");
                ImgCountText.Text = "无法访问该文件所在的磁盘";
                MessageBox.Show(this,
                    "无法访问这张图片所在的磁盘：\n\n" + item.Path +
                    "\n\n请确认移动硬盘 / 网络位置已连接后重试（记录未做改动）。",
                    "打开图片", MessageBoxButton.OK, MessageBoxImage.Warning);
                return Task.CompletedTask;
            }
            if (string.IsNullOrEmpty(item.Path) || !File.Exists(item.Path))
            {
                Diag2($"OPEN missing file '{item.Path}'");
                ImgCountText.Text = "文件不存在：" + (item.Filename ?? item.Path);
                MessageBox.Show(this,
                    "这张图片在磁盘上已不存在，无法打开：\n\n" + (item.Path ?? "") +
                    "\n\n该记录已从图片库中清理。",
                    "打开图片", MessageBoxButton.OK, MessageBoxImage.Warning);
                _ = RefreshImgLibraryAsync();
                return Task.CompletedTask;
            }

            // 先出窗口：不依赖任何查询，点击即响应
            var win = new ImgViewerWindow(item, _img, _recognizer, _db, null)
            {
                Owner = this
            };
            win.Show();
            win.Activate();
            Diag2("OPEN shown");

            // 序列（上一张/下一张）稍后补上，绝不能反过来阻塞窗口出现。
            // 0.62.4：证件照 / 文档页也附加序列（此前只有 all 模式有，导致这两个页里左右键无效）
            // 0.60.1：人物相册 / 标签相册同样附上（否则这两个相册里 ←/→ 也是死的）
            if (_imgMode == "all") _ = AttachViewerSequenceAsync(win, item, BuildImgQuery());
            else if (_imgMode == "idPhotos" || _imgMode == "docPhotos")
            {
                var kw = _imgMode == "idPhotos"
                    ? ImageLibraryService.IdPhotoKeywords
                    : ImageLibraryService.DocKeywords;
                _ = AttachViewerKeywordSequenceAsync(win, item, kw);
            }
            else if (_imgMode == "personPhotos")
                _ = AttachViewerPersonSequenceAsync(win, item, _currentPersonId);
            else if (_imgMode == "tagPhotos")
                _ = AttachViewerTagSequenceAsync(win, item, _currentTagId);
        }
        catch (Exception ex)
        {
            try
            {
                var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pb_scroll_diag.txt");
                System.IO.File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} open fail: {ex}\n");
            }
            catch { }
            Diag2("OPEN fail " + ex.Message);
            MessageBox.Show(this, "打开图片失败：\n" + ex.Message, "图片查看器",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        return Task.CompletedTask;
    }

    /// <summary>后台算出当前筛选的完整序列与名次，再附加到已打开的查看器；超 2.5 秒只记日志，窗口照常可用。</summary>
    private async Task AttachViewerSequenceAsync(ImgViewerWindow win, ImgItem item, ImageLibraryService.ImgQuery fq)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var task = Task.Run(() =>
            {
                var total = _img.QueryCount(fq);
                if (total <= 0) return (Total: total, Rank: -1, Seq: (ImageLibraryService.IImgSequence?)null);
                ImageLibraryService.IImgSequence seq = new ImgSeq(_img, fq, total);
                int rank;
                try { rank = seq.RankOf(item.Path); } catch { rank = -1; }
                return (Total: total, Rank: rank, Seq: seq);
            });
            if (await Task.WhenAny(task, Task.Delay(2500)) != task)
                Diag2($"OPEN seq slow (>{sw.ElapsedMilliseconds}ms)，窗口已先行打开");
            var (total, rank, seq) = await task;
            Diag2($"OPEN seq total={total} rank={rank} in {sw.ElapsedMilliseconds}ms");
            if (seq != null) win.AttachSequence(seq, rank);
        }
        catch (Exception ex)
        {
            Diag2($"OPEN seq err {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>证件照 / 文档页的导航序列：按标签族关键词结果随机访问（0.62.4）。</summary>
    private async Task AttachViewerKeywordSequenceAsync(ImgViewerWindow win, ImgItem item, string[] keywords)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var task = Task.Run(() =>
            {
                var total = _img.CountByTagKeywords(keywords);
                if (total <= 0) return (Total: total, Rank: -1, Seq: (ImageLibraryService.IImgSequence?)null);
                ImageLibraryService.IImgSequence seq = new ImgKwSeq(_img, keywords, total);
                int rank;
                try { rank = seq.RankOf(item.Path); } catch { rank = -1; }
                return (Total: total, Rank: rank, Seq: seq);
            });
            if (await Task.WhenAny(task, Task.Delay(2500)) != task)
                Diag2($"OPEN kwseq slow (>{sw.ElapsedMilliseconds}ms)，窗口已先行打开");
            var (total, rank, seq) = await task;
            Diag2($"OPEN kwseq total={total} rank={rank} in {sw.ElapsedMilliseconds}ms");
            if (seq != null) win.AttachSequence(seq, rank);
        }
        catch (Exception ex)
        {
            Diag2($"OPEN kwseq err {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>人物相册的导航序列（0.60.1：此前未附，←/→ 在人物相册里无效）。</summary>
    private async Task AttachViewerPersonSequenceAsync(ImgViewerWindow win, ImgItem item, long personId)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var task = Task.Run(() =>
            {
                var total = _img.PersonPhotoCount(personId);
                if (total <= 0) return (Total: total, Rank: -1, Seq: (ImageLibraryService.IImgSequence?)null);
                ImageLibraryService.IImgSequence seq = new ImgPersonSeq(_img, personId, total);
                int rank;
                try { rank = seq.RankOf(item.Path); } catch { rank = -1; }
                return (Total: total, Rank: rank, Seq: seq);
            });
            if (await Task.WhenAny(task, Task.Delay(2500)) != task)
                Diag2($"OPEN personseq slow (>{sw.ElapsedMilliseconds}ms)，窗口已先行打开");
            var (total, rank, seq) = await task;
            Diag2($"OPEN personseq total={total} rank={rank} in {sw.ElapsedMilliseconds}ms");
            if (seq != null) win.AttachSequence(seq, rank);
        }
        catch (Exception ex)
        {
            Diag2($"OPEN personseq err {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>标签相册的导航序列（0.60.1：此前未附，←/→ 在标签相册里无效）。</summary>
    private async Task AttachViewerTagSequenceAsync(ImgViewerWindow win, ImgItem item, long tagId)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var task = Task.Run(() =>
            {
                var total = _img.TagPhotoCount(tagId);
                if (total <= 0) return (Total: total, Rank: -1, Seq: (ImageLibraryService.IImgSequence?)null);
                ImageLibraryService.IImgSequence seq = new ImgTagSeq(_img, tagId, total);
                int rank;
                try { rank = seq.RankOf(item.Path); } catch { rank = -1; }
                return (Total: total, Rank: rank, Seq: seq);
            });
            if (await Task.WhenAny(task, Task.Delay(2500)) != task)
                Diag2($"OPEN tagseq slow (>{sw.ElapsedMilliseconds}ms)，窗口已先行打开");
            var (total, rank, seq) = await task;
            Diag2($"OPEN tagseq total={total} rank={rank} in {sw.ElapsedMilliseconds}ms");
            if (seq != null) win.AttachSequence(seq, rank);
        }
        catch (Exception ex)
        {
            Diag2($"OPEN tagseq err {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>主网格键盘：Enter 打开、Delete 删除、F2 重命名、Space 多选勾选、Esc 退多选/清搜索、Ctrl+A 全选、Ctrl+C 复制路径。</summary>
    private async void ImgGrid_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_imgMode is not ("all" or "personPhotos" or "tagPhotos" or "idPhotos" or "docPhotos")) return;
        var cur = _lastImgFocus;
        var mods = System.Windows.Input.Keyboard.Modifiers;
        switch (e.Key)
        {
            case System.Windows.Input.Key.Enter:
                if (cur != null && File.Exists(cur.Path)) await OpenImgViewerAsync(cur);
                e.Handled = true;
                break;
            case System.Windows.Input.Key.Delete:
                // 多选有勾选 → 批量删除；否则删当前焦点卡片
                if (_imgMulti && _imgSel.Count > 0)
                    ImgSelDelete_Click(sender, new RoutedEventArgs());
                else if (cur != null)
                    await DeleteImgItemAsync(cur);
                e.Handled = true;
                break;
            case System.Windows.Input.Key.F2:
                if (cur != null) await RenameImgItemAsync(cur);
                e.Handled = true;
                break;
            case System.Windows.Input.Key.C when (mods & System.Windows.Input.ModifierKeys.Control) != 0:
                if (cur != null && !string.IsNullOrEmpty(cur.Path))
                {
                    try { Clipboard.SetText(cur.Path); ImgCountText.Text = "已复制路径"; }
                    catch { }
                }
                e.Handled = true;
                break;
            case System.Windows.Input.Key.Space:
                if (_imgMulti && cur != null)
                {
                    cur.Selected = !cur.Selected;
                    if (cur.Selected) _imgSel.Add(cur.Path); else _imgSel.Remove(cur.Path);
                    UpdateImgSelCount();
                }
                e.Handled = true;
                break;
            case System.Windows.Input.Key.Escape:
                if (_imgMulti) { _imgMulti = false; UpdateImgMultiUI(); }
                else if (!string.IsNullOrEmpty(ImgSearchBox.Text)) ImgSearchBox.Text = "";
                e.Handled = true;
                break;
            case System.Windows.Input.Key.A when (mods & System.Windows.Input.ModifierKeys.Control) != 0:
                if (_imgMulti) ImgSelAll_Click(sender, new RoutedEventArgs());
                e.Handled = true;
                break;
        }
    }

    /// <summary>键盘/共用：删除单张图片（回收站）。</summary>
    private async Task DeleteImgItemAsync(ImgItem item)
    {
        if (item == null || !File.Exists(item.Path)) return;
        var confirm = MessageBox.Show(this,
            $"确定删除「{item.Filename}」？\n将移入回收站。（Del）", "删除图片",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;
        var wasActive = IsActive;
        try
        {
            FileOps.DeleteToRecycleBin(item.Path, _hwnd);
            _img.RemoveIndexEntry(item.Path);
            _img.RemoveImgTagEntry(item.Path);
            try { _img.PruneEmptyPersons(); } catch { }
            ImgCountText.Text = "已删除";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "删除失败：" + ex.Message, "删除", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        if (wasActive && !IsActive) Activate();
        _imgPage = 0;
        await LoadImgPageAsync();
    }

    /// <summary>键盘/共用：重命名单张图片。</summary>
    private async Task RenameImgItemAsync(ImgItem item)
    {
        if (item == null || !File.Exists(item.Path)) return;
        var dlg = new InputBoxWindow("重命名", "新文件名：", Path.GetFileName(item.Path)) { Owner = this };
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.Value))
        {
            var newName = dlg.Value.Trim();
            if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                MessageBox.Show(this, "文件名包含非法字符。", "重命名", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var dir = Path.GetDirectoryName(item.Path)!;
            var newPath = Path.Combine(dir, newName);
            if (string.Equals(item.Path, newPath, StringComparison.OrdinalIgnoreCase)) return;
            if (File.Exists(newPath))
            {
                MessageBox.Show(this, "同名文件已存在。", "重命名", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            try
            {
                File.Move(item.Path, newPath);
                _img.RenameIndexEntry(item.Path, newPath);
                ImgCountText.Text = "已重命名";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "重命名失败：" + ex.Message, "重命名", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            _imgPage = 0;
            await LoadImgPageAsync();
        }
    }

    // ---- 图片操作（右键菜单） ----

    private static ImgItem? ContextItem(object sender)
        => (sender as MenuItem)?.DataContext as ImgItem
           ?? ((sender as FrameworkElement)?.DataContext as ImgItem);

    private void ImgOpen_Click(object sender, RoutedEventArgs e)
    {
        var item = ContextItem(sender);
        if (item == null || !File.Exists(item.Path)) return;
        try { Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true }); } catch { }
    }

    private void ImgOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var item = ContextItem(sender);
        if (item == null) return;
        try
        {
            var dir = Path.GetDirectoryName(item.Path);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.Path}\"") { UseShellExecute = true });
        }
        catch { }
    }

    private void ImgCopyPath_Click(object sender, RoutedEventArgs e)
    {
        var item = ContextItem(sender);
        if (item == null) return;
        Clipboard.SetText(item.Path);
        ImgCountText.Text = "已复制路径";
        _ = Task.Delay(1500).ContinueWith(_ => Dispatcher.InvokeAsync(async () =>
        {
            _imgPage = 0;
            await LoadImgPageAsync();
        }));
    }

    private void ImgCopyDesc_Click(object sender, RoutedEventArgs e)
    {
        var item = ContextItem(sender);
        if (item == null) return;
        if (string.IsNullOrEmpty(item.Description))
        {
            ImgCountText.Text = "该图片暂无描述";
            return;
        }
        Clipboard.SetText(item.Description);
        ImgCountText.Text = "已复制描述";
    }

    private void ImgCopyFile_Click(object sender, RoutedEventArgs e)
    {
        var item = ContextItem(sender);
        if (item == null || !File.Exists(item.Path)) return;
        try
        {
            var files = new System.Collections.Specialized.StringCollection { item.Path };
            Clipboard.SetFileDropList(files);
            ImgCountText.Text = "已复制图片文件";
        }
        catch
        {
            ImgCountText.Text = "复制失败";
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool SystemParametersInfo(int uiAction, int uiParam, string pvParam, int fWinIni);

    private void ImgSetWallpaper_Click(object sender, RoutedEventArgs e)
    {
        var item = ContextItem(sender);
        if (item == null || !File.Exists(item.Path)) return;
        try
        {
            var path = Path.GetFullPath(item.Path);
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", writable: true);
            key?.SetValue("WallpaperStyle", "10"); // 适应屏幕
            key?.SetValue("TileWallpaper", "0");
            SystemParametersInfo(20, 0, path, 0x01 | 0x02); // SPI_SETDESKWALLPAPER + SPIF_UPDATEINIFILE|SPIF_SENDCHANGE
            ImgCountText.Text = "已设为桌面壁纸";
        }
        catch
        {
            ImgCountText.Text = "设置壁纸失败";
        }
    }

    /// <summary>单张人脸识别：调 imgtag /api/faces/scan（image_ids 模式）</summary>
    private async void ImgFaceOne_Click(object sender, RoutedEventArgs e)
    {
        var item = ContextItem(sender);
        if (item == null) return;
        if (_recognizing) { ImgCountText.Text = "识别任务进行中…"; return; }
        _recognizing = true;
        ImgRecognizeBtn.IsEnabled = false;
        ImgTagBtn.IsEnabled = false;
        ImgCountText.Text = "人脸识别中…";
        try
        {
            if (!_recognizer.ServerRunning() && !_recognizer.EnsureServer(out _))
            {
                ImgCountText.Text = "识别服务不可用";
                return;
            }
            var progress = new Action<string>(m => Dispatcher.Invoke(() => ImgCountText.Text = m));
            var (faces, err) = await Task.Run(() => _recognizer.ScanFaceOneAsync(item.Path, progress));
            if (!string.IsNullOrEmpty(err)) { ImgCountText.Text = "识别失败：" + err; return; }
            ImgCountText.Text = $"识别完成：检出 {faces} 张人脸";
            _tray?.Notify("PictureButler · 人脸识别", $"{item.Filename}\n检出 {faces} 张人脸");
            ShowNotice($"人脸识别完成：{item.Filename} 检出 {faces} 张人脸");
            await RefreshImgLibraryAsync();
        }
        catch (Exception ex)
        {
            ImgCountText.Text = "识别出错：" + ex.Message;
        }
        finally
        {
            _recognizing = false;
            ImgRecognizeBtn.IsEnabled = true;
            ImgTagBtn.IsEnabled = true;
        }
    }

    /// <summary>单张 AI 打标：调 imgtag /api/process（image_ids 模式，需 LM Studio）</summary>
    private async void ImgTagOne_Click(object sender, RoutedEventArgs e)
    {
        var item = ContextItem(sender);
        if (item == null) return;
        if (_recognizing) { ImgCountText.Text = "识别任务进行中…"; return; }
        if (!_recognizer.LmStudioAvailable())
        {
            ImgCountText.Text = "AI 打标需要 LM Studio（127.0.0.1:1234）";
            _tray?.Notify("PictureButler", "AI 打标需要 LM Studio（127.0.0.1:1234）");
            return;
        }
        _recognizing = true;
        ImgRecognizeBtn.IsEnabled = false;
        ImgTagBtn.IsEnabled = false;
        ImgCountText.Text = "AI 打标中…";
        try
        {
            if (!_recognizer.ServerRunning() && !_recognizer.EnsureServer(out _))
            {
                ImgCountText.Text = "识别服务不可用";
                return;
            }
            var progress = new Action<string>(m => Dispatcher.Invoke(() => ImgCountText.Text = m));
            var (desc, tags, err) = await Task.Run(() => _recognizer.AiTagOneAsync(item.Path, progress));
            if (!string.IsNullOrEmpty(err)) { ImgCountText.Text = "打标失败：" + err; return; }
            ImgCountText.Text = "AI 打标完成";
            ShowNotice($"AI 打标完成：{item.Filename}");
            _tray?.Notify("PictureButler · AI 打标", $"{item.Filename}\n{desc}");
            await RefreshImgLibraryAsync();
        }
        catch (Exception ex)
        {
            ImgCountText.Text = "打标出错：" + ex.Message;
        }
        finally
        {
            _recognizing = false;
            ImgRecognizeBtn.IsEnabled = true;
            ImgTagBtn.IsEnabled = true;
        }
    }

    private async void ImgRename_Click(object sender, RoutedEventArgs e)
    {
        var item = ContextItem(sender);
        if (item != null) await RenameImgItemAsync(item);
    }

    private async void ImgDelete_Click(object sender, RoutedEventArgs e)
    {
        var item = ContextItem(sender);
        if (item != null) await DeleteImgItemAsync(item);
    }

    private void ApplyFilter()
    {
        try
        {
        var selId = _selectedItem?.Id;
        IEnumerable<PromptItem> query = _allItems;
        if (!string.IsNullOrEmpty(_searchText))
        {
            query = query.Where(x =>
                x.Title.Contains(_searchText, StringComparison.OrdinalIgnoreCase) ||
                x.Content.Contains(_searchText, StringComparison.OrdinalIgnoreCase));
        }
        var filtered = query.OrderByDescending(x => ParseDate(x.UpdatedAt)).ToList();
        PromptGrid.ItemsSource = filtered;
        CountText.Text = $"共 {filtered.Count} 条";
        if (filtered.Count == 0)
        {
            if (!string.IsNullOrEmpty(_searchText))
                ShowPromptEmpty("没有匹配的提示词", $"没有找到包含「{_searchText}」的标题或内容", "清空搜索", () => { if (SearchBox != null) SearchBox.Text = ""; });
            else
                ShowPromptEmpty("还没有提示词", "点右上角「＋ 新建」开始积累你的提示词库", "＋ 新建", () => NewPrompt_Click(this, new RoutedEventArgs()));
        }
        else HidePromptEmpty();
        // 搜索结果中未加载过预览图的项补加载（已加载的自动跳过）
        if (filtered.Any(i => i.PreviewImage == null && i.HasPreviewImage == true))
        {
            _ = LoadPreviewImagesAsync(filtered);
        }

        // 恢复/默认选中，保证详情面板跟随
        PromptItem? target = null;
        if (selId != null)
        {
            target = filtered.FirstOrDefault(x => x.Id == selId) ?? filtered.FirstOrDefault();
        }
        target ??= filtered.FirstOrDefault();
        if (target != null)
        {
            ShowDetail(target);
            var id = target.Id;
            Dispatcher.BeginInvoke(() => HighlightSelectedCard(id));
        }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("ApplyFilter: " + ex);
        }
    }

    private static DateTime ParseDate(string? iso)
        => DateTime.TryParse(iso, out var dt) ? dt : DateTime.MinValue;

    // ---- 卡片点击选中 ----
    private void PromptCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border b && b.DataContext is PromptItem item)
        {
            if (_promptMulti)
            {
                if (FindAncestor<CheckBox>(e.OriginalSource as DependencyObject) != null) return;
                item.Selected = !item.Selected;
                UpdatePromptSelCount();
                e.Handled = true;
                return;
            }
            ShowDetail(item);
            SelectCard(b);
        }
    }

    // ---- 提示词卡片右键菜单（0.46.1 补齐：默认视图「提示词」也能右键触发）----
    private static PromptItem? PromptCtxItem(object sender)
        => (sender as MenuItem)?.DataContext as PromptItem
           ?? (sender as FrameworkElement)?.DataContext as PromptItem;

    /// <summary>右键菜单统一入口：把被点卡片同步为当前项（详情栏 + 卡片高亮），再复用详情栏动作。</summary>
    private bool PromptCtxFocus(object sender)
    {
        var item = PromptCtxItem(sender);
        if (item == null) return false;
        ShowDetail(item);
        HighlightSelectedCard(item.Id);
        return true;
    }

    private void PromptCtxCopyTitle_Click(object sender, RoutedEventArgs e)
    {
        if (PromptCtxFocus(sender)) CopyTitle_Click(sender, e);
    }

    private void PromptCtxCopyContent_Click(object sender, RoutedEventArgs e)
    {
        if (PromptCtxFocus(sender)) CopyContent_Click(sender, e);
    }

    private void PromptCtxEdit_Click(object sender, RoutedEventArgs e)
    {
        if (PromptCtxFocus(sender)) EditPrompt_Click(sender, e);
    }

    private void PromptCtxDelete_Click(object sender, RoutedEventArgs e)
    {
        if (PromptCtxFocus(sender)) DeletePrompt_Click(sender, e);
    }

    private void SelectCard(Border b)
    {
        if (_selectedCard != null)
        {
            _selectedCard.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x19, 0x1D, 0x28));
            _selectedCard.BorderBrush = System.Windows.Media.Brushes.Transparent;
        }
        b.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x24, 0x2E, 0x44));
        b.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x5B, 0x8D, 0xEF));
        _selectedCard = b;
    }

    private void HighlightSelectedCard(string id)
    {
        if (PromptGrid.ItemsSource is not System.Collections.IEnumerable list) return;
        foreach (var item in list)
        {
            if (item is PromptItem pi && pi.Id == id)
            {
                var container = PromptGrid.ItemContainerGenerator.ContainerFromItem(pi);
                if (container is FrameworkElement fe)
                {
                    var b = FindVisualChild<Border>(fe);
                    if (b != null) SelectCard(b);
                }
                return;
            }
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            var found = FindVisualChild<T>(child);
            if (found != null) return found;
        }
        return null;
    }

    // ---- 星标切换 ----
    private void StarBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is PromptItem item)
        {
            var wasSelected = _selectedItem?.Id == item.Id;
            _db.ToggleStar(item.Id);
            item.Starred = !item.Starred;
            ApplyFilter();
            if (wasSelected)
            {
                ShowDetail(item);
                HighlightSelectedCard(item.Id);
            }
        }
    }

    private void DetailStarBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedItem == null) return;
        var id = _selectedItem.Id;
        _db.ToggleStar(id);
        _selectedItem.Starred = !_selectedItem.Starred;
        ApplyFilter();
        ShowDetail(_selectedItem);
        HighlightSelectedCard(id);
    }

    // ---- 详情面板 ----
    private PromptItem? _selectedItem;

    private void ShowDetail(PromptItem item)
    {
        if (item == null)
        {
            _selectedItem = null;
            DetailPanel.Visibility = Visibility.Collapsed;
            return;
        }
        var wasHidden = DetailPanel.Visibility != Visibility.Visible;
        _selectedItem = item;
        DetailPanel.Visibility = Visibility.Visible;
        if (wasHidden) FadeIn(DetailPanel, 180);   // 只在「面板从无到有」时淡入；连续切换条目不重复动画
        DetailTitle.Text = item.Title;
        DetailStarBtn.Foreground = item.Starred ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF5, 0xB8, 0x4B))
                                                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4A, 0x51, 0x63));
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(item.Category)) parts.Add(item.Category);
        if (!string.IsNullOrEmpty(item.UpdatedAt)) parts.Add("更新 " + item.UpdatedDisplay);
        if (item.Tags is { Count: > 0 }) parts.Add(string.Join(" · ", item.Tags));
        DetailMeta.Text = string.Join("  ·  ", parts);
        DetailContent.Text = item.Content;
        DetailPreview.Source = null;
        GalleryList.ItemsSource = null;
        GalleryLabel.Visibility = Visibility.Collapsed;

        // 异步加载图片（后台线程拼接分块）
        _ = LoadDetailImagesAsync(item.Id);
    }

    private async Task LoadDetailImagesAsync(string id)
    {
        var preview = await Task.Run(() => _db.LoadPreviewImage(id));
        var gallery = await Task.Run(() => _db.LoadGalleryImages(id));
        await Dispatcher.InvokeAsync(() =>
        {
            if (_selectedItem?.Id != id) return; // 已切到别的条目
            if (!string.IsNullOrEmpty(preview) && preview.StartsWith("data:image"))
            {
                DetailPreview.Source = DataUrlToBitmap(preview);
            }
            if (gallery.Count > 0)
            {
                GalleryLabel.Visibility = Visibility.Visible;
                var sources = gallery.Where(g => g.StartsWith("data:image"))
                                     .Select(DataUrlToBitmap)
                                     .Where(b => b != null)
                                     .ToList();
                GalleryList.ItemsSource = sources;
            }
        });
    }

    private static System.Windows.Media.Imaging.BitmapImage? DataUrlToBitmap(string dataUrl)
    {
        try
        {
            var comma = dataUrl.IndexOf(',');
            if (comma < 0) return null;
            var b64 = dataUrl[(comma + 1)..];
            var bytes = Convert.FromBase64String(b64);
            using var ms = new MemoryStream(bytes);
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private void CopyContent_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedItem == null) return;
        Clipboard.SetText(_selectedItem.Content);
        CountText.Text = "已复制内容";
        _ = Task.Delay(1500).ContinueWith(_ => Dispatcher.InvokeAsync(() => CountText.Text = $"共 {_allItems.Count} 条"));
    }

    private void CopyTitle_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedItem == null) return;
        Clipboard.SetText(_selectedItem.Title);
        CountText.Text = "已复制标题";
        _ = Task.Delay(1500).ContinueWith(_ => Dispatcher.InvokeAsync(() => CountText.Text = $"共 {_allItems.Count} 条"));
    }

    // ---- 新建 / 编辑 / 删除 ----

    private void NewPrompt_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new PromptEditWindow(null, _db) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Result != null)
        {
            _db.SaveItem(dlg.Result, isNew: true);
            _allItems.Insert(0, dlg.Result);
            ApplyFilter();
            CountText.Text = $"共 {_allItems.Count} 条";
            ShowDetail(dlg.Result);
            Dispatcher.BeginInvoke(() => HighlightSelectedCard(dlg.Result.Id));
        }
    }

    private void EditPrompt_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedItem == null) return;
        var dlg = new PromptEditWindow(_selectedItem, _db) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Result != null)
        {
            // 图片删空后选择同时删除提示词
            if (dlg.Result.ShouldDelete)
            {
                _db.DeleteItem(dlg.Result);
                _allItems.RemoveAll(x => x.Id == dlg.Result.Id);
                ApplyFilter();
                CountText.Text = $"共 {_allItems.Count} 条";
                MarkPromptFileSetDirty();
                ShowDetail(null);
                ShowNotice($"已删除提示词（卡片图片已删空）");
                return;
            }
            _db.SaveItem(dlg.Result, isNew: false);
            var idx = _allItems.FindIndex(x => x.Id == dlg.Result.Id);
            if (idx >= 0) _allItems[idx] = dlg.Result;
            ApplyFilter();
            CountText.Text = $"共 {_allItems.Count} 条";
            ShowDetail(dlg.Result);
            Dispatcher.BeginInvoke(() => HighlightSelectedCard(dlg.Result.Id));
        }
    }

    private void DeletePrompt_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedItem == null) return;
        var confirm = MessageBox.Show(this,
            $"确定删除「{_selectedItem.Title}」？\n此操作不可恢复。", "删除提示词",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;
        var id = _selectedItem.Id;
        _db.DeleteItem(_selectedItem);
        _allItems.RemoveAll(x => x.Id == id);
        ApplyFilter();
        CountText.Text = $"共 {_allItems.Count} 条";
        MarkPromptFileSetDirty();
    }

    // ==================== 批量多选（提示词 + 图片库） ====================

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject d) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var child = VisualTreeHelper.GetChild(d, i);
            if (child is T t) yield return t;
            foreach (var sub in FindVisualChildren<T>(child)) yield return sub;
        }
    }

    // ---- 提示词多选 ----

    private void PromptMulti_Click(object sender, RoutedEventArgs e)
    {
        _promptMulti = !_promptMulti;
        UpdatePromptMultiUI();
    }

    private void UpdatePromptMultiUI()
    {
        MultiModeVisible = _promptMulti || _imgMulti;
        if (_promptMulti)
        {
            PromptMultiBar.Visibility = Visibility.Visible;
            PromptNewBtn.Visibility = Visibility.Collapsed;
            PromptMultiBtn.Content = "退出多选";
        }
        else
        {
            PromptMultiBar.Visibility = Visibility.Collapsed;
            PromptNewBtn.Visibility = Visibility.Visible;
            PromptMultiBtn.Content = "多选";
            foreach (var x in _allItems) x.Selected = false;
        }
        UpdatePromptSelCount();
    }

    private void UpdatePromptSelCount()
    {
        var n = _allItems.Count(x => x.Selected);
        PromptSelCount.Text = $"已选 {n} 项";
        PromptSelDelBtn.IsEnabled = n > 0;
        PromptSelAllBtn.Content = n > 0 && n >= CurrentPromptCount() ? "取消全选" : "全选";
    }

    private int CurrentPromptCount()
    {
        if (PromptGrid.ItemsSource is IEnumerable<PromptItem> list) return list.Count();
        return _allItems.Count;
    }

    private void PromptSelAll_Click(object sender, RoutedEventArgs e)
    {
        IEnumerable<PromptItem> list = _allItems;
        if (PromptGrid.ItemsSource is IEnumerable<PromptItem> cur) list = cur;
        var arr = list.ToList();
        if (arr.Count == 0) return;
        var all = arr.All(x => x.Selected);
        foreach (var x in arr) x.Selected = !all;
        UpdatePromptSelCount();
    }

    private void PromptMultiCancel_Click(object sender, RoutedEventArgs e)
    {
        _promptMulti = false;
        UpdatePromptMultiUI();
    }

    private async void PromptSelDelete_Click(object sender, RoutedEventArgs e)
    {
        var sel = _allItems.Where(x => x.Selected).ToList();
        if (sel.Count == 0) return;
        var confirm = MessageBox.Show(this,
            $"确定删除所选 {sel.Count} 条提示词？\n此操作不可恢复。", "批量删除提示词",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;
        foreach (var item in sel)
        {
            try { _db.DeleteItem(item); } catch { }
        }
        _allItems.RemoveAll(x => x.Selected);
        MarkPromptFileSetDirty();
        ApplyFilter();
        CountText.Text = $"共 {_allItems.Count} 条";
        ShowDetail(null);
        _promptMulti = false;
        UpdatePromptMultiUI();
        ShowNotice($"已删除 {sel.Count} 条提示词");
    }

    // ---- 图片库多选 ----

    private void ImgMulti_Click(object sender, RoutedEventArgs e)
    {
        Diag2($"MULTI-BTN new={!_imgMulti}");
        _imgMulti = !_imgMulti;
        UpdateImgMultiUI();
    }

    private void UpdateImgMultiUI()
    {
        MultiModeVisible = _promptMulti || _imgMulti;
        // 「移出该人物」只在「人物详情」上下文中出现
        if (ImgSelRemovePersonBtn != null)
            ImgSelRemovePersonBtn.Visibility = (_imgMode == "personPhotos" && _currentPersonId > 0)
                ? Visibility.Visible : Visibility.Collapsed;
        if (_imgMulti)
        {
            ImgMultiBar.Visibility = Visibility.Visible;
            ImgMultiBtn.Content = "退出多选";
        }
        else
        {
            ImgMultiBar.Visibility = Visibility.Collapsed;
            ImgMultiBtn.Content = "多选";
            _imgSel.Clear();
            // 数据层清空选中（虚拟化下离屏卡片没有可视化 CheckBox，必须操作数据）
            foreach (var it in CurrentImgItems()) it.Selected = false;
        }
        UpdateImgSelCount();
        ApplyImgToolbarMode();
    }

    /// <summary>
    /// 图片库工具条模式（0.61.2 批次 2c）：常规态与多选态共用同一 48px 槽位，
    /// 进入多选是「整条替换」而不是「在旁边追加一行」，因此没有高度跳动。
    /// 组内条目的显隐仍按各自状态决定，避免出现空工具条或两条并排。
    /// </summary>
    private void ApplyImgToolbarMode()
    {
        if (ImgToolMain == null || ImgToolMulti == null) return;
        var multi = _imgMulti || _personMulti;
        ImgToolMain.Visibility = multi ? Visibility.Collapsed : Visibility.Visible;
        ImgToolMulti.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;
        if (ImgMultiBar != null)
            ImgMultiBar.Visibility = _imgMulti ? Visibility.Visible : Visibility.Collapsed;
        if (PersonMultiBar != null)
            PersonMultiBar.Visibility = _personMulti ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ImgChk_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox chk && chk.DataContext is ImgItem it)
        {
            Diag2($"CHK-CARD sel={it.Selected}");
            if (it.Selected) _imgSel.Add(it.Path);
            else _imgSel.Remove(it.Path);
            UpdateImgSelCount();
        }
    }

    /// <summary>当前图片网格绑定的数据项（不依赖可视化容器，虚拟化安全）</summary>
    private IEnumerable<ImgItem> CurrentImgItems()
        => ImgGrid.ItemsSource is IEnumerable<ImgItem> list ? list : Enumerable.Empty<ImgItem>();

    private void UpdateImgSelCount()
    {
        var n = _imgSel.Count;
        ImgSelCount.Text = $"已选 {n} 项";
        ImgSelDelBtn.IsEnabled = n > 0;
        var page = CurrentImgItems().ToList();
        var sel = page.Count(x => x.Selected);
        ImgSelAllBtn.Content = page.Count > 0 && sel >= page.Count ? "取消全选" : "全选";
    }

    private void ImgSelAll_Click(object sender, RoutedEventArgs e)
    {
        var page = CurrentImgItems().ToList();
        if (page.Count == 0) return;
        bool all = page.All(x => x.Selected);
        foreach (var it in page)
        {
            it.Selected = !all;
            if (!all) _imgSel.Add(it.Path); else _imgSel.Remove(it.Path);
        }
        UpdateImgSelCount();
    }

    private void ImgMultiCancel_Click(object sender, RoutedEventArgs e)
    {
        _imgMulti = false;
        UpdateImgMultiUI();
    }

    private async void ImgSelDelete_Click(object sender, RoutedEventArgs e)
    {
        var sel = _imgSel.ToList();
        if (sel.Count == 0) return;
        var confirm = MessageBox.Show(this,
            $"确定删除所选 {sel.Count} 张图片？\n将移入回收站。", "批量删除图片",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;
        var wasActive = IsActive;
        var ok = 0;
        foreach (var path in sel)
        {
            try
            {
                if (File.Exists(path)) FileOps.DeleteToRecycleBin(path, _hwnd);
                _img.RemoveIndexEntry(path);
                _img.RemoveImgTagEntry(path);
                ok++;
            }
            catch { }
        }
        // 兜底：万一 Shell 仍抢走了前台，把窗口拉回来（仅在原本确实处于激活状态时）
        if (wasActive && !IsActive) Activate();
        try { _img.PruneEmptyPersons(); } catch { } // 批量删脸后收敛空人物
        _imgSel.Clear();
        _imgMulti = false;
        _imgPage = 0;
        await LoadImgPageAsync();
        UpdateImgMultiUI();
        ShowNotice($"已删除 {ok} 张图片");
    }

    // ==================== 人物：忽略 / 把照片移出人物 ====================

    /// <summary>人物右键菜单「忽略该人物」：忽略后不显示、不画框、不参与后续识别匹配</summary>
    private void PersonIgnore_Click(object sender, RoutedEventArgs e)
    {
        var pid = ResolvePersonId(sender);
        if (pid == null) return;
        var p = CurrentPerson(pid.Value);
        var name = string.IsNullOrWhiteSpace(p?.Name) ? "该人物" : p!.Name;
        var confirm = MessageBox.Show(this,
            $"忽略「{name}」？\n\n忽略后：该人物不再出现在人物列表、不再在图片上标注人脸框，" +
            $"后续人脸识别也不会再把它识别出来。\n\n可在「设置 → 已忽略人物」中随时恢复。",
            "忽略人物", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;
        if (_img.SetPersonIgnored(pid.Value, true))
        {
            _personSel.Remove(pid.Value);
            if (_imgMode == "personPhotos" && _currentPersonId == pid.Value)
            {
                HidePersonActionBar();
                _imgMode = "persons";
                _currentPersonId = 0;
            }
            _ = LoadImgPageAsync();
            RefreshIgnoredPersons();
            ShowNotice($"已忽略「{name}」，可在设置页恢复");
        }
        else
        {
            MessageBox.Show(this, "操作失败（识别库不可用）", "忽略人物", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>已忽略人物列表是否展开（默认收起：忽略条目多时避免列表过长）</summary>
    private bool _ignoredExpanded;

    /// <summary>设置页：加载已忽略人物列表（默认收起，只显示数量）</summary>
    private void RefreshIgnoredPersons()
    {
        if (IgnoredPersonList == null || IgnoredToggle == null) return;
        List<PersonItem> list;
        try { list = _img.IgnoredPersons(); } catch { list = new List<PersonItem>(); }

        IgnoredPersonList.ItemsSource = list;
        bool any = list.Count > 0;
        IgnoredEmptyText.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        IgnoredEmptyText.Text = "没有被忽略的人物";
        IgnoredToggle.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        IgnoredCountText.Text = $"已忽略人物（{list.Count}）";
        ApplyIgnoredExpandState();
    }

    /// <summary>按当前展开状态刷新折叠头与列表的可见性</summary>
    private void ApplyIgnoredExpandState()
    {
        bool show = _ignoredExpanded && IgnoredToggle.Visibility == Visibility.Visible;
        IgnoredScroll.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        IgnoredArrow.Text = show ? "▾" : "▸";
        IgnoredToggleHint.Text = show ? "点击收起" : "点击展开";
    }

    /// <summary>点击折叠头：展开 / 收起已忽略人物列表</summary>
    private void IgnoredToggle_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _ignoredExpanded = !_ignoredExpanded;
        ApplyIgnoredExpandState();
    }

    /// <summary>设置页：恢复某个被忽略的人物</summary>
    private void RestoreIgnoredPerson_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is long id)
        {
            if (_img.SetPersonIgnored(id, false))
            {
                RefreshIgnoredPersons();
                _ = LoadImgPageAsync();
                ShowNotice("已恢复该人物");
            }
        }
    }

    /// <summary>人物多选：批量忽略所选人物</summary>
    private void PersonSelIgnore_Click(object sender, RoutedEventArgs e)
    {
        if (_personSel.Count == 0) return;
        var ids = _personSel.ToList();
        var names = _img.Persons().Where(p => ids.Contains(p.Id)).Select(p => p.Name).Take(3).ToList();
        var preview = string.Join("、", names);
        if (ids.Count > 3) preview += $" 等 {ids.Count} 人";

        var confirm = MessageBox.Show(this,
            $"忽略所选的 {ids.Count} 个人物？\n（{preview}）\n\n" +
            "忽略后：不再出现在人物列表、不再在图片上标注人脸框，后续人脸识别也不会再识别出来。\n" +
            "可在「设置 → 已忽略人物」中随时恢复。",
            "批量忽略人物", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        int ok = 0;
        foreach (var id in ids) { if (_img.SetPersonIgnored(id, true)) ok++; }

        _personSel.Clear();
        _personMulti = false;
        UpdatePersonMultiUI();
        _ = LoadImgPageAsync();
        RefreshIgnoredPersons();
        ShowNotice($"已忽略 {ok} 个人物，可在设置页恢复");
    }

    /// <summary>按人脸相似度分析并给出合并建议</summary>
    private async void MergeSuggest_Click(object sender, RoutedEventArgs e)
    {
        if (MergeSuggestBtn == null) return;
        var oldContent = MergeSuggestBtn.Content;
        MergeSuggestBtn.IsEnabled = false;
        MergeSuggestBtn.Content = "分析中…";
        List<MergeSuggestion> sugg;
        try { sugg = await Task.Run(() => _img.SuggestMerges()); }
        catch { sugg = new List<MergeSuggestion>(); }
        MergeSuggestBtn.Content = oldContent;
        MergeSuggestBtn.IsEnabled = true;

        var dlg = new MergeSuggestWindow(sugg, _imgTagDir, _img) { Owner = this };
        dlg.ShowDialog();
        if (dlg.MergedPairs > 0)
        {
            _ = LoadImgPageAsync();
            ShowNotice($"已按建议合并 {dlg.MergedPairs} 组人物");
        }
    }

    /// <summary>图片右键菜单：把这张照片从当前人物移出</summary>
    private async void ImgCtxRemovePerson_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.DataContext is ImgItem it && !string.IsNullOrWhiteSpace(it.Path))
            await RemoveFromCurrentPersonAsync(new[] { it.Path });
    }

    /// <summary>图片多选条：把所选照片从当前人物移出</summary>
    private async void ImgSelRemovePerson_Click(object sender, RoutedEventArgs e)
    {
        if (_imgSel.Count == 0) return;
        await RemoveFromCurrentPersonAsync(_imgSel.ToList());
    }

    /// <summary>把指定照片移出当前人物：只删「这些照片 × 该人物」的人脸标注，不删照片</summary>
    private async Task RemoveFromCurrentPersonAsync(IReadOnlyCollection<string> paths)
    {
        if (_currentPersonId <= 0 || paths.Count == 0) return;
        var p = CurrentPerson(_currentPersonId);
        var name = string.IsNullOrWhiteSpace(p?.Name) ? "当前人物" : p!.Name;
        var confirm = MessageBox.Show(this,
            $"把所选 {paths.Count} 张照片从「{name}」中移出？\n\n" +
            "只移除这些照片上属于该人物的人脸标注，照片本身不会被删除，其他人物也不受影响。",
            "移出该人物", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        var pid = _currentPersonId;
        var n = await Task.Run(() => _img.RemoveFacesFromPerson(pid, paths));

        _imgSel.Clear();
        _imgMulti = false;
        UpdateImgMultiUI();
        await LoadImgPageAsync();
        ShowNotice(n > 0 ? $"已从「{name}」移出 {paths.Count} 张照片" : "这些人脸已被移除过");
    }

    /// <summary>图片卡片右键菜单展开时，按上下文决定「从这个人物中移除」是否可见</summary>
    private void ImgCardCtx_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu cm)
        {
            var item = cm.Items.OfType<MenuItem>().FirstOrDefault(m => m.Name == "ImgCtxRemovePersonItem");
            if (item != null)
                item.Visibility = (_imgMode == "personPhotos" && _currentPersonId > 0)
                    ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private bool _imgBatchRunning;

    private async void ImgSelFace_Click(object sender, RoutedEventArgs e) => await RunSelBatchAsync(true);

    private async void ImgSelTag_Click(object sender, RoutedEventArgs e) => await RunSelBatchAsync(false);

    /// <summary>多选批量识别/打标：所选路径 → imgtag image_ids 队列，带进度、服务校验、完成刷新与空人物收敛。</summary>
    private async Task RunSelBatchAsync(bool face)
    {
        if (_imgBatchRunning) return;
        var paths = _imgSel.ToList();
        if (paths.Count == 0) { ShowNotice("请先选择图片"); return; }

        if (face)
        {
            // 人脸检测：只用 ONNX，不依赖 AI 打标引擎
        }
        else
        {
            if (!_recognizer.ServerRunning() && !_recognizer.EnsureServer(out var em))
            { ImgRecognizeStatus.Text = string.IsNullOrEmpty(em) ? "识别服务不可用" : em; return; }
            if (!_recognizer.LmStudioAvailable())
            { ImgRecognizeStatus.Text = "AI 打标需要 LM Studio（127.0.0.1:1234），当前未运行"; return; }
        }

        _imgBatchRunning = true;
        SetBatchButtons(false);
        if (ImgTagStopBtn != null) { ImgTagStopBtn.Visibility = Visibility.Visible; ImgTagStopBtn.IsEnabled = true; }
        var progress = new Action<string>(m => Dispatcher.Invoke(() => ImgRecognizeStatus.Text = m));
        try
        {
            ImgRecognizeStatus.Text = face ? $"准备识别所选 {paths.Count} 张…" : $"准备打标所选 {paths.Count} 张…";
            var result = face
                ? await Task.Run(() => _recognizer.ScanFacesByPathsAsync(paths, progress))
                : await Task.Run(() => _recognizer.AiTagByPathsAsync(paths, progress));
            if (!result.Ok)
            {
                ImgRecognizeStatus.Text = (face ? "人脸识别失败：" : "AI 打标失败：") + result.Error;
                ShowNotice(ImgRecognizeStatus.Text);
                return;
            }
            if (face)
            {
                // 聚类可能新建人物、也可能让旧人物变空，批量识别后收敛一次空人物
                try { await Task.Run(() => _img.PruneEmptyPersons()); } catch { }
                ImgRecognizeStatus.Text = $"识别完成：扫描 {result.Scanned} 张，检出人脸 {result.Faces}，失败 {result.Failed}";
            }
            else
            {
                ImgRecognizeStatus.Text = $"AI 打标完成：处理 {result.Scanned} 张，失败 {result.Failed}";
            }
            ShowNotice(ImgRecognizeStatus.Text);
            _imgMulti = false;
            UpdateImgMultiUI();
            await LoadImgPageAsync();
        }
        catch (Exception ex)
        {
            ImgRecognizeStatus.Text = "批量处理出错：" + ex.Message;
        }
        finally
        {
            _imgBatchRunning = false;
            SetBatchButtons(true);
            if (ImgTagStopBtn != null) ImgTagStopBtn.Visibility = Visibility.Collapsed;
            UpdateImgSelCount();
        }
    }

    private void SetBatchButtons(bool on)
    {
        ImgSelFaceBtn.IsEnabled = on;
        ImgSelTagBtn.IsEnabled = on;
        ImgSelDelBtn.IsEnabled = on;
        ImgRecognizeBtn.IsEnabled = on;
        ImgTagBtn.IsEnabled = on;
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Escape) return;
        if (_promptMulti) { _promptMulti = false; UpdatePromptMultiUI(); e.Handled = true; }
        else if (_imgMulti) { _imgMulti = false; UpdateImgMultiUI(); e.Handled = true; }
    }

    // ---- 标题栏交互 ----
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }
        DragMove();
    }

    private void MinBtn_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaxBtn_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    // ---- 托盘 / 热键 ----
    /// <summary>二次启动 / 托盘 / 热键共用：把主窗口唤到前台。</summary>
    public void ShowFromTrayOrRestore() => ShowMainWindow();

    private void ShowMainWindow()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false; // 触发置顶
        Focus();
    }

    private void QuitApp()
    {
        _reallyQuit = true;
        _tray?.Dispose();
        _hotkey?.Dispose();
        _http?.Dispose();
        // 释放进程内识别引擎（imgtag_native：连接池 + ONNX 会话 + 异步运行时）
        try { ImgtagRecognizer.Shutdown(); } catch { }
        Close();
        Application.Current.Shutdown();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_reallyQuit)
        {
            // 关闭 → 最小化到托盘（HTTP 服务保持常驻）
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }
}
