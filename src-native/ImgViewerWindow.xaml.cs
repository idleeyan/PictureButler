using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PictureButler;

public partial class ImgViewerWindow : Window
{
    private ImgItem _item;
    private readonly ImageLibraryService? _lib;
    private readonly ImgtagRecognizer? _recognizer;
    private readonly DataService? _db;
    /// <summary>当前导航序列。可为 null（单张浏览）；打开后由 AttachSequence 异步补上。</summary>
    private ImageLibraryService.IImgSequence? _seq;
    private int _rank = -1;              // 当前图在完整结果中的 0-based 序号，-1 表示游离单图
    private int _ghostRemoved;           // 本次浏览中清理掉的幽灵（外部已删除）数量，用于修正总数显示
    private int EffectiveTotal => _seq == null ? 0 : Math.Max(1, _seq.Total - _ghostRemoved);
    private bool _sizedOnce;             // 窗口尺寸只按首张图调整一次
    private bool _navBusy;
    private double _zoom = 1.0;
    private bool _fitActive = true;   // 默认适应窗口；手动缩放后退出
    private Point _dragStart;
    private bool _dragging;
    private double _scrollStartH, _scrollStartV;
    private bool _busy;

    public ImgViewerWindow(ImgItem item, ImageLibraryService? lib = null, ImgtagRecognizer? recognizer = null, DataService? db = null, ImageLibraryService.IImgSequence? seq = null)
    {
        InitializeComponent();
        _item = item;
        _lib = lib;
        _recognizer = recognizer;
        _db = db;
        _seq = seq;
        Owner = Application.Current.MainWindow;
        // 首开时视口可能尚未布局，布局完成后补一次适应
        Loaded += (_, _) => { if (_fitActive) TryFitToWindow(); };

        if (_seq != null)
        {
            try
            {
                var r = _seq.RankOf(item.Path);
                _rank = r > 0 ? r - 1 : -1;
            }
            catch { _rank = -1; }
        }
        UpdateNavUi();
        LoadCurrent();
    }

    /// <summary>
    /// 关闭查看器时把激活权**显式交还主窗口**（0.62.2）。
    /// 查看器是 Show() 出来的非模态窗口，关闭时 Windows 只会激活 z-order 里的「下一个」窗口，
    /// 那个窗口很可能是别的程序 —— 表现为主窗口被压到底层。设了 Owner 也不够，必须手动 Activate。
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        try
        {
            if (Owner is Window owner && owner.IsLoaded)
            {
                if (owner.WindowState == WindowState.Minimized) owner.WindowState = WindowState.Normal;
                owner.Activate();
                owner.Focus();
            }
        }
        catch { }
    }

    /// <summary>
    /// 打开后补上导航序列（rank 为 1-based 名次，已在后台算好）。
    /// 看点：先无条件把窗口显示出来，序列稍后再算，任何数据库操作都不阻塞「点开图片」。
    /// </summary>
    public void AttachSequence(ImageLibraryService.IImgSequence? seq, int oneBasedRank)
    {
        if (seq == null || _seq != null) return;
        _seq = seq;
        _rank = oneBasedRank > 0 ? oneBasedRank - 1 : -1;
        UpdateNavUi();
        UpdatePos();
    }

    /// <summary>载入 _item：重置缩放/人脸框，先用缩略图占位，后台换原图并叠加人脸框（切换图复用同一窗口）。</summary>
    private void LoadCurrent()
    {
        ImgTitle.Text = _item.Filename;
        _fitActive = true;
        TryFitToWindow();
        FaceCanvas.Children.Clear();
        FullImage.Source = null;
        _metaParts.Clear(); MetaText.Text = ""; PromptText.Text = ""; StatusText.Text = "";
        RenderMeta();
        UpdatePos();

        // 原图到达前先用磁盘缩略图放大占位，避免空白
        try
        {
            var ph = ThumbCache.GetOrCreate(_item.Path, 560, out _);
            if (ph != null)
            {
                FullImage.Source = ph;
                if (_fitActive) TryFitToWindow();
            }
        }
        catch { }

        var path = _item.Path;
        // 后台加载原图 + 人脸框
        Task.Run(() =>
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(path);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();

                var faces = _lib?.FacesByPath(path) ?? new List<FaceBox>();
                var exifOrient = GetExifOrientation(path);

                Dispatcher.InvokeAsync(() =>
                {
                    if (!string.Equals(path, _item.Path, StringComparison.OrdinalIgnoreCase)) return; // 已切走
                    FullImage.Source = bmp;
                    if (!_sizedOnce)
                    {
                        _sizedOnce = true;
                        // 左右布局：左图 + 右侧 300 信息栏；按首张图尺寸调整窗口，不再为底部信息预留高度
                        var w = Math.Max(520, Math.Min(bmp.PixelWidth, 1000));
                        var h = Math.Max(320, Math.Min(bmp.PixelHeight, 620));
                        Width = Math.Min(1600, w + 320);
                        Height = 40 + h + 48;
                        var wa = SystemParameters.WorkArea;
                        if (Height > wa.Height - 80) Height = wa.Height - 80;
                        if (Width > wa.Width - 80) Width = wa.Width - 80;
                        ImgScroll.UpdateLayout();
                    }
                    // 原图尺寸与缩略图不同、首开时视口可能尚未布局，到达后按当前模式重算
                    if (_fitActive) TryFitToWindow();
                    if (faces.Count > 0)
                    {
                        DrawFaces(faces, bmp.PixelWidth, bmp.PixelHeight, exifOrient);
                        AddMeta("人脸", $"{faces.Count} 个");
                        AddMeta("EXIF方向", exifOrient.ToString());
                        if (faces.Count > 0)
                        {
                            var f0 = faces[0];
                            var (tx, ty, tw, th) = TransformBbox(f0.X, f0.Y, f0.Width, f0.Height, exifOrient);
                            AddMeta("首框原始", $"({f0.X:F3}, {f0.Y:F3}) {f0.Width:F3}×{f0.Height:F3}");
                            AddMeta("首框变换后", $"({tx:F3}, {ty:F3}) {tw:F3}×{th:F3}");
                        }
                    }
                    else
                    {
                        AddMeta("EXIF方向", exifOrient.ToString());
                    }
                });
            }
            catch (Exception ex)
            {
                // 原图解码失败不再静默：在底部状态栏明确写出原因
                try
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        if (string.Equals(path, _item.Path, StringComparison.OrdinalIgnoreCase))
                            StatusText.Text = "图片加载失败：" + ex.Message;
                    });
                }
                catch { }
            }
        });
    }

    // ==================== 序列导航 ====================

    private void UpdateNavUi()
    {
        var has = _seq != null && _rank >= 0;
        PrevBtn.Visibility = NextBtn.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        PosText.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        if (has)
        {
            PrevBtn.IsEnabled = _rank > 0;
            NextBtn.IsEnabled = _rank < EffectiveTotal - 1;
        }
    }

    private void UpdatePos()
    {
        if (_seq != null && _rank >= 0) PosText.Text = $"{_rank + 1} / {EffectiveTotal}";
    }

    private async void Navigate(int delta)
    {
        if (_seq == null || _rank < 0 || _navBusy) return;
        _navBusy = true;
        try
        {
            var found = await FindExistingAsync(_rank + delta, delta);
            if (found == null) { StatusText.Text = delta > 0 ? "已经是最后一张" : "已经是第一张"; return; }
            _rank = found.Value.rank; _item = found.Value.item;
            LoadCurrent(); UpdateNavUi();
        }
        finally { _navBusy = false; }
    }

    private async void GotoRank(int rank)
    {
        if (_seq == null || _rank < 0 || _navBusy) return;
        _navBusy = true;
        try
        {
            rank = Math.Clamp(rank, 0, _seq.Total - 1);
            // Home 从头部向后找、End 从尾部向前找第一张真实存在的图
            var dir = rank <= 0 ? 1 : -1;
            var found = await FindExistingAsync(rank, dir);
            if (found == null) return;
            _rank = found.Value.rank; _item = found.Value.item;
            LoadCurrent(); UpdateNavUi();
        }
        finally { _navBusy = false; }
    }

    /// <summary>从 start 沿 dir 方向找第一张磁盘上真实存在的图；途中清理幽灵记录（外部已删除）。</summary>
    private async Task<(int rank, ImgItem item)?> FindExistingAsync(int start, int dir)
    {
        if (_seq == null) return null;
        var total = _seq.Total;
        var ghosts = new List<string>();
        var hit = await Task.Run(() =>
        {
            int r = start;
            ImgItem? live = null;
            while (r >= 0 && r < total)
            {
                var it = _seq.At(r);
                if (it == null) break;
                if (File.Exists(it.Path)) { live = it; break; }
                ghosts.Add(it.Path);
                r += dir;
            }
            return (r, live);
        });
        if (ghosts.Count > 0)
        {
            _ghostRemoved += ghosts.Count;
            foreach (var g in ghosts)
            {
                try { _lib?.RemoveIndexEntry(g); _lib?.RemoveImgTagEntry(g); } catch { }
            }
            if (Owner is MainWindow mw) _ = mw.RefreshImgLibraryAsync();
        }
        return hit.live != null ? (hit.r, hit.live) : null;
    }

    private void Prev_Click(object sender, RoutedEventArgs e) => Navigate(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => Navigate(1);

    // ==================== 信息显示 ====================

    private readonly List<string> _metaParts = new();

    private void AddMeta(string label, string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        _metaParts.Add($"{label}: {value}");
        MetaText.Text = string.Join("\n", _metaParts);
    }

    private void RenderMeta()
    {
        _metaParts.Clear();
        AddMeta("文件", _item.Filename);
        AddMeta("路径", _item.Path);
        AddMeta("尺寸", _item.SizeDisplay);
        AddMeta("大小", _item.FileSizeDisplay);
        AddMeta("来源", _item.Source == "imgtag" ? "本地识别库" : "文件夹");

        // 创建时间（识别库）
        var created = _lib?.GetImgTagCreatedAt(_item.Path) ?? "";
        if (!string.IsNullOrEmpty(created)) AddMeta("入库时间", created);

        // 人脸识别状态
        AddMeta("人脸识别", _item.FaceScanned ? "已扫描" : "未扫描");

        // AI 打标状态 + 标签
        if (!string.IsNullOrEmpty(_item.Tags)) AddMeta("标签", _item.Tags);

        // 关联提示词（按文件名匹配提示词 generationInfo.filenames）
        if (_item.HasPrompt || _db != null)
        {
            var titles = new List<string>();
            try
            {
                if (_db != null)
                {
                    foreach (var p in _db.LoadItems())
                    {
                        if (p.GenerationInfo is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Object)
                        {
                            CollectPromptFiles(je, _item.Filename, p.Title, titles);
                            if (je.TryGetProperty("generations", out var gens) && gens.ValueKind == System.Text.Json.JsonValueKind.Array)
                            {
                                foreach (var g in gens.EnumerateArray())
                                    if (g.ValueKind == System.Text.Json.JsonValueKind.Object) CollectPromptFiles(g, _item.Filename, p.Title, titles);
                            }
                        }
                    }
                }
            }
            catch { }
            if (titles.Count > 0)
            {
                PromptText.Text = "关联提示词：" + string.Join("  /  ", titles.Distinct().Take(3));
                PromptText.ToolTip = string.Join("\n", titles.Distinct());
            }
            else
            {
                PromptText.Text = "关联提示词：无";
            }
        }

        // AI 描述
        DescText.Text = string.IsNullOrEmpty(_item.Description) ? "（无 AI 描述）" : "AI 描述：" + _item.Description;
    }

    private static void CollectPromptFiles(System.Text.Json.JsonElement obj, string filename, string title, List<string> titles)
    {
        if (obj.TryGetProperty("filenames", out var fn) && fn.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var f in fn.EnumerateArray())
            {
                var name = f.GetString();
                if (!string.IsNullOrEmpty(name) && string.Equals(name, filename, StringComparison.OrdinalIgnoreCase))
                {
                    titles.Add(title);
                    return;
                }
            }
        }
    }

    // ==================== 缩放与平移 ====================

    private void Image_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (FullImage.Source == null) return;
        var old = _zoom;
        // Ctrl+滚轮：更精细的步长
        var fine = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var step = fine ? 1.06 : 1.15;
        var delta = e.Delta > 0 ? step : 1 / step;
        _fitActive = false;
        _zoom = Math.Clamp(_zoom * delta, 0.1, 8.0);
        var ratio = _zoom / old;
        // 以鼠标位置为缩放锚点：保持光标下的内容点不动
        var pos = e.GetPosition(ImgScroll);
        var h = ImgScroll.HorizontalOffset;
        var v = ImgScroll.VerticalOffset;
        ApplyZoom();
        ImgScroll.UpdateLayout();
        ImgScroll.ScrollToHorizontalOffset(h * ratio + pos.X * (1 - ratio));
        ImgScroll.ScrollToVerticalOffset(v * ratio + pos.Y * (1 - ratio));
        e.Handled = true;
    }

    private void ApplyZoom()
    {
        // 必须用 LayoutTransform：它改变布局尺寸，ScrollViewer 的滚动范围随之扩大，
        // 放大后的图片才能滚到边缘、拖拽才能平移（RenderTransform 只放大渲染，滚动范围不变）
        // 对统一容器 ImgHost 做 LayoutTransform：图片与人脸框一起缩放、滚动范围同步扩大
        HostTransform.ScaleX = _zoom;
        HostTransform.ScaleY = _zoom;
        ZoomText.Text = $"{(int)Math.Round(_zoom * 100)}%";
    }

    /// <summary>按当前视口把图片等比缩到完整可见；视口未就绪时静默跳过（稍后重试）。</summary>
    private void TryFitToWindow()
    {
        if (FullImage.Source is not BitmapSource b) return;
        var vw = ImgScroll.ViewportWidth - 48;
        var vh = ImgScroll.ViewportHeight - 48;
        if (vw <= 0 || vh <= 0) return;
        _zoom = Math.Clamp(Math.Min(vw / b.PixelWidth, vh / b.PixelHeight), 0.1, 8.0);
        ApplyZoom();
        ImgScroll.ScrollToHome();
    }

    /// <summary>双击在“适应窗口”与“100%”之间切换。</summary>
    private void Image_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FullImage.Source is not BitmapSource) return;
        if (_fitActive) { _fitActive = false; _zoom = 1.0; ApplyZoom(); ImgScroll.ScrollToHome(); }
        else
        {
            _fitActive = true;
            TryFitToWindow();
        }
    }

    private void Image_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { Image_DoubleClick(sender, e); e.Handled = true; return; }
        if (e.ChangedButton == MouseButton.Left || e.ChangedButton == MouseButton.Middle)
        {
            _dragging = true;
            _dragStart = e.GetPosition(this);
            _scrollStartH = ImgScroll.HorizontalOffset;
            _scrollStartV = ImgScroll.VerticalOffset;
            FullImage.CaptureMouse();
            e.Handled = true;
        }
    }

    private void Image_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = e.GetPosition(this);
        var dx = p.X - _dragStart.X;
        var dy = p.Y - _dragStart.Y;
        ImgScroll.ScrollToHorizontalOffset(_scrollStartH - dx);
        ImgScroll.ScrollToVerticalOffset(_scrollStartV - dy);
    }

    private void Image_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            FullImage.ReleaseMouseCapture();
        }
    }

    // ==================== 原图叠加人脸框 ====================

    private void DrawFaces(List<FaceBox> faces, int imgW, int imgH)
    {
        DrawFaces(faces, imgW, imgH, exifOrientation: 1);
    }

    /// <summary>按 EXIF 方向变换 bbox 坐标后绘制人脸框。
    /// WPF 显示图片时会自动应用 EXIF 旋转，但检测 bbox 是原始像素坐标，需要对齐。
    /// orientation: 1=正常, 3=180°, 6=顺时针90°, 8=逆时针90°</summary>
    private void DrawFaces(List<FaceBox> faces, int imgW, int imgH, int exifOrientation)
    {
        FaceCanvas.Children.Clear();
        FaceCanvas.Width = imgW;
        FaceCanvas.Height = imgH;
        foreach (var f in faces)
        {
            var (x, y, w, h) = TransformBbox(f.X, f.Y, f.Width, f.Height, exifOrientation);
            var px = x * imgW;
            var py = y * imgH;
            var pw = w * imgW;
            var ph = h * imgH;
            var rect = new System.Windows.Shapes.Rectangle
            {
                Width = Math.Max(2, pw),
                Height = Math.Max(2, ph),
                Stroke = new SolidColorBrush(Color.FromRgb(0xF5, 0xB8, 0x4B)),
                StrokeThickness = 2
            };
            Canvas.SetLeft(rect, px);
            Canvas.SetTop(rect, py);
            FaceCanvas.Children.Add(rect);

            if (!string.IsNullOrEmpty(f.Name))
            {
                var tb = new TextBlock
                {
                    Text = f.Name,
                    Foreground = new SolidColorBrush(Colors.White),
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Background = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)),
                    Padding = new Thickness(4, 1, 4, 1)
                };
                Canvas.SetLeft(tb, px);
                Canvas.SetTop(tb, Math.Max(0, py - 22));
                FaceCanvas.Children.Add(tb);
            }
        }
    }

    /// <summary>EXIF bbox 坐标变换（输入输出均为 0-1 相对比例）。
    /// orientation: 1=正常, 2=水平翻转, 3=180°, 4=垂直翻转, 5=transpose, 6=顺时针90°, 7=transverse, 8=逆时针90°</summary>
    private static (double x, double y, double w, double h) TransformBbox(double x, double y, double w, double h, int orientation)
    {
        switch (orientation)
        {
            case 1: return (x, y, w, h);                           // 正常
            case 2: return (1 - x - w, y, w, h);                   // 水平翻转
            case 3: return (1 - x - w, 1 - y - h, w, h);           // 180°
            case 4: return (x, 1 - y - h, w, h);                   // 垂直翻转
            case 5: return (y, x, h, w);                           // 转置（对角线翻转）
            case 6: return (y, 1 - x - w, h, w);                   // 顺时针 90°
            case 7: return (1 - y - h, 1 - x - w, h, w);           // 横向翻转 + 顺时针 90°
            case 8: return (1 - y - h, x, h, w);                   // 逆时针 90°
            default: return (x, y, w, h);
        }
    }

    /// <summary>从图片文件读取 EXIF 方向（1-8），读取失败返回 1</summary>
    private static int GetExifOrientation(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var bmp = BitmapFrame.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (bmp.Metadata is BitmapMetadata meta && meta.ContainsQuery("System.Photo.Orientation"))
            {
                var val = meta.GetQuery("System.Photo.Orientation");
                if (val is ushort orient && orient >= 1 && orient <= 8)
                    return orient;
            }
        }
        catch { }
        return 1;
    }

    // ==================== 标题栏 / 关闭 ====================

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        switch (e.Key)
        {
            case System.Windows.Input.Key.Escape: Close(); e.Handled = true; break;
            case System.Windows.Input.Key.Delete: Delete_Click(sender, e); e.Handled = true; break;
            case System.Windows.Input.Key.F2: Rename_Click(sender, e); e.Handled = true; break;
            case System.Windows.Input.Key.C when (mods & ModifierKeys.Control) != 0:
                CopyPath_Click(sender, e); e.Handled = true; break;
            case System.Windows.Input.Key.Left:
            case System.Windows.Input.Key.A when (mods & ModifierKeys.Control) == 0:
                Navigate(-1); e.Handled = true; break;
            case System.Windows.Input.Key.Right:
            case System.Windows.Input.Key.D when (mods & ModifierKeys.Control) == 0:
                Navigate(1); e.Handled = true; break;
            case System.Windows.Input.Key.Home: GotoRank(0); e.Handled = true; break;
            case System.Windows.Input.Key.End: GotoRank(_seq?.Total - 1 ?? 0); e.Handled = true; break;
            case System.Windows.Input.Key.OemPlus when (mods & ModifierKeys.Control) != 0:
            case System.Windows.Input.Key.Add when (mods & ModifierKeys.Control) != 0:
                _fitActive = false; _zoom = Math.Clamp(_zoom * 1.2, 0.1, 8.0); ApplyZoom(); e.Handled = true; break;
            case System.Windows.Input.Key.OemMinus when (mods & ModifierKeys.Control) != 0:
            case System.Windows.Input.Key.Subtract when (mods & ModifierKeys.Control) != 0:
                _fitActive = false; _zoom = Math.Clamp(_zoom / 1.2, 0.1, 8.0); ApplyZoom(); e.Handled = true; break;
            case System.Windows.Input.Key.D0 when (mods & ModifierKeys.Control) != 0:
            case System.Windows.Input.Key.NumPad0 when (mods & ModifierKeys.Control) != 0:
                _fitActive = false; _zoom = 1.0; ApplyZoom(); e.Handled = true; break;
            case System.Windows.Input.Key.F when (mods & ModifierKeys.Control) != 0:
            case System.Windows.Input.Key.D1 when (mods & ModifierKeys.Control) != 0:
                _fitActive = true; TryFitToWindow(); e.Handled = true; break;
        }
    }

    // ==================== 文件操作 ====================

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(_item.Path) { UseShellExecute = true }); } catch { }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Path.GetDirectoryName(_item.Path);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_item.Path}\"") { UseShellExecute = true });
        }
        catch { }
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(_item.Path);
        StatusText.Text = "已复制路径";
    }

    private void CopyDesc_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_item.Description))
        {
            Clipboard.SetText(_item.Description);
            StatusText.Text = "已复制描述";
        }
        else StatusText.Text = "该图片暂无描述";
    }

    private void CopyFile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var files = new System.Collections.Specialized.StringCollection { _item.Path };
            Clipboard.SetFileDropList(files);
            StatusText.Text = "已复制图片文件";
        }
        catch { StatusText.Text = "复制失败"; }
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new InputBoxWindow("重命名", "新文件名：", Path.GetFileName(_item.Path)) { Owner = this };
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.Value))
        {
            var newName = dlg.Value.Trim();
            if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                MessageBox.Show(this, "文件名包含非法字符。", "重命名", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var dir = Path.GetDirectoryName(_item.Path)!;
            var newPath = Path.Combine(dir, newName);
            if (string.Equals(_item.Path, newPath, StringComparison.OrdinalIgnoreCase)) return;
            if (File.Exists(newPath))
            {
                MessageBox.Show(this, "同名文件已存在。", "重命名", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            try
            {
                File.Move(_item.Path, newPath);
                _lib?.RenameIndexEntry(_item.Path, newPath);
                _item.Path = newPath;
                _item.Filename = newName;
                ImgTitle.Text = newName;
                StatusText.Text = "已重命名";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "重命名失败：" + ex.Message, "重命名", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(this,
            $"确定删除「{_item.Filename}」？\n将移入回收站。", "删除图片",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;
        try
        {
            // 传本窗口句柄，避免 Shell 激活别处把窗口压到底层（0.62.1）
            FileOps.DeleteToRecycleBin(_item.Path, new System.Windows.Interop.WindowInteropHelper(this).Handle);
            _lib?.RemoveIndexEntry(_item.Path);
            _lib?.RemoveImgTagEntry(_item.Path);
            if (Owner is MainWindow mw) _ = mw.RefreshImgLibraryAsync();
            // 有序列时：删除后该名次已顺延为下一张，原地续览；没有下一张才关闭
            if (_seq != null && _rank >= 0)
            {
                var nxt = _seq.At(_rank);
                if (nxt != null && File.Exists(nxt.Path))
                {
                    _item = nxt; LoadCurrent(); UpdateNavUi();
                    return;
                }
            }
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "删除失败：" + ex.Message, "删除", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ==================== 识别人脸 / AI 打标 ====================

    private async void FaceOne_Click(object sender, RoutedEventArgs e)
    {
        if (_recognizer == null) { StatusText.Text = "识别服务不可用"; return; }
        if (_busy) { StatusText.Text = "任务进行中…"; return; }
        _busy = true;
        StatusText.Text = "人脸识别中…";
        try
        {
            // 人脸检测不依赖 AI 打标 / LM Studio
            var progress = new Action<string>(m => Dispatcher.Invoke(() => StatusText.Text = m));
            var (faces, err) = await Task.Run(() => _recognizer.ScanFaceOneAsync(_item.Path, progress));
            if (!string.IsNullOrEmpty(err)) { StatusText.Text = "识别失败：" + err; return; }
            StatusText.Text = $"识别完成：检出 {faces} 张人脸（人物已归类）";
            AddMeta("人脸识别", "已扫描");
            AddMeta("人脸", $"{faces} 个");
            var bmp = FullImage.Source as BitmapImage;
            if (bmp != null)
            {
                var faceBoxes = _lib?.FacesByPath(_item.Path) ?? new List<FaceBox>();
                var exif = GetExifOrientation(_item.Path);
                DrawFaces(faceBoxes, bmp.PixelWidth, bmp.PixelHeight, exif);
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "识别出错：" + ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    private async void TagOne_Click(object sender, RoutedEventArgs e)
    {
        if (_recognizer == null) { StatusText.Text = "识别服务不可用"; return; }
        if (_busy) { StatusText.Text = "任务进行中…"; return; }
        if (!_recognizer.LmStudioAvailable())
        {
            StatusText.Text = "AI 打标需要 LM Studio（127.0.0.1:1234）";
            return;
        }
        _busy = true;
        StatusText.Text = "AI 打标中…";
        try
        {
            if (!_recognizer.ServerRunning() && !_recognizer.EnsureServer(out _))
            {
                StatusText.Text = "识别服务不可用";
                return;
            }
            var progress = new Action<string>(m => Dispatcher.Invoke(() => StatusText.Text = m));
            var (desc, tags, err) = await Task.Run(() => _recognizer.AiTagOneAsync(_item.Path, progress));
            if (!string.IsNullOrEmpty(err)) { StatusText.Text = "打标失败：" + err; return; }
            StatusText.Text = "AI 打标完成";
            _item.Description = desc;
            _item.Tags = tags;
            DescText.Text = "AI 描述：" + (string.IsNullOrEmpty(desc) ? "（无）" : desc);
            AddMeta("标签", tags);
        }
        catch (Exception ex)
        {
            StatusText.Text = "打标出错：" + ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    private void SetWallpaper_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            const uint SPI_SETDESKWALLPAPER = 20;
            const uint SPIF_UPDATEINIFILE = 0x01;
            const uint SPIF_SENDCHANGE = 0x02;
            NativeMethods.SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, _item.Path, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
            StatusText.Text = "已设为桌面壁纸";
        }
        catch (Exception ex)
        {
            StatusText.Text = "设置壁纸失败：" + ex.Message;
        }
    }
}
