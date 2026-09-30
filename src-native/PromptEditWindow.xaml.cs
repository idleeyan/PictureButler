using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace PictureButler;

/// <summary>gallery 缩略图项（含原始 index，Tag 使用）</summary>
public sealed class GalleryThumbItem
{
    public int Index { get; init; }
    public BitmapImage Thumb { get; init; } = null!;
}

/// <summary>提示词编辑/新建窗口（模态），支持编辑卡片封面（更换 / 移除 / 从已有图片选择）与删除任意图片</summary>
public partial class PromptEditWindow : Window
{
    private readonly PromptItem? _original;
    private readonly DataService? _db;
    public PromptItem? Result { get; private set; }

    /// <summary>待应用的封面操作：null=不变；dataUrl=更换；"__remove__"=移除</summary>
    private string? _pendingCover;

    /// <summary>已删除的 gallery 原始 index（保存时一次性应用）</summary>
    private readonly HashSet<int> _removedGallery = new();

    public PromptEditWindow(PromptItem? item = null, DataService? db = null)
    {
        InitializeComponent();
        _original = item;
        _db = db;
        WindowTitleText.Text = item == null ? "新建提示词" : "编辑提示词";
        if (item != null)
        {
            TitleBox.Text = item.Title;
            ContentBox.Text = item.Content;
            TagsBox.Text = item.Tags is { Count: > 0 } ? string.Join(", ", item.Tags) : "";
            CategoryBox.Text = item.Category ?? "";
            StarredChk.IsChecked = item.Starred;
            LoadCoverAndGallery(item.Id);
        }
        else
        {
            // 新建：支持先选封面再填内容
            CoverPlaceholder.Text = "无封面（可选）";
        }
        Loaded += (_, _) => TitleBox.Focus();
    }

    /// <summary>加载当前封面与已有图片（gallery）</summary>
    private void LoadCoverAndGallery(string id)
    {
        try
        {
            if (_db == null) return;
            var cover = _db.LoadPreviewImage(id);
            if (!string.IsNullOrEmpty(cover))
            {
                var bmp = DataUrlToScaledBitmap(cover, 260);
                if (bmp != null)
                {
                    CoverImage.Source = bmp;
                    CoverImage.Visibility = Visibility.Visible;
                    CoverPlaceholder.Visibility = Visibility.Collapsed;
                }
            }
            // 从已有图片选封面（gallery）
            var gallery = _db.LoadGalleryItems(id);
            if (gallery.Count > 0)
            {
                var thumbs = new List<GalleryThumbItem>();
                foreach (var g in gallery)
                {
                    var t = DataUrlToScaledBitmap(g.DataUrl, 200);
                    if (t != null) thumbs.Add(new GalleryThumbItem { Index = g.Index, Thumb = t });
                }
                if (thumbs.Count > 0)
                {
                    GalleryLabel.Visibility = Visibility.Visible;
                    GalleryPick.Visibility = Visibility.Visible;
                    GalleryPick.ItemsSource = thumbs;
                }
            }
        }
        catch { }
    }

    /// <summary>dataUrl 解码并按长边缩放（GDI+，JPEG 兼容），返回冻结的 BitmapImage</summary>
    private static BitmapImage? DataUrlToScaledBitmap(string dataUrl, int maxSide)
    {
        try
        {
            var comma = dataUrl.IndexOf(',');
            if (comma < 0) return null;
            var bytes = Convert.FromBase64String(dataUrl[(comma + 1)..]);
            using var ms = new MemoryStream(bytes);
            using var src = System.Drawing.Image.FromStream(ms);
            int w = src.Width, h = src.Height;
            if (Math.Max(w, h) > maxSide)
            {
                double k = maxSide / (double)Math.Max(w, h);
                w = Math.Max(1, (int)(w * k));
                h = Math.Max(1, (int)(h * k));
            }
            using var bmp = new System.Drawing.Bitmap(w, h);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                g.DrawImage(src, 0, 0, w, h);
            }
            using var outMs = new MemoryStream();
            bmp.Save(outMs, System.Drawing.Imaging.ImageFormat.Png);
            outMs.Position = 0;
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.StreamSource = outMs;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }

    /// <summary>本地图片文件 → 封面 dataUrl（长边 ≤512，JPEG 压缩）</summary>
    private static string? FileToCoverDataUrl(string path)
    {
        try
        {
            using var src = System.Drawing.Image.FromFile(path);
            const int maxSide = 512;
            int w = src.Width, h = src.Height;
            if (Math.Max(w, h) > maxSide)
            {
                double k = maxSide / (double)Math.Max(w, h);
                w = Math.Max(1, (int)(w * k));
                h = Math.Max(1, (int)(h * k));
            }
            using var bmp = new System.Drawing.Bitmap(w, h);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                g.DrawImage(src, 0, 0, w, h);
            }
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
            return "data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray());
        }
        catch { return null; }
    }

    /// <summary>gallery 图（dataUrl）→ 封面 dataUrl（长边 ≤512）</summary>
    private static string? GalleryToCoverDataUrl(string dataUrl)
    {
        try
        {
            var comma = dataUrl.IndexOf(',');
            if (comma < 0) return null;
            var bytes = Convert.FromBase64String(dataUrl[(comma + 1)..]);
            using var ms = new MemoryStream(bytes);
            using var src = System.Drawing.Image.FromStream(ms);
            const int maxSide = 512;
            int w = src.Width, h = src.Height;
            if (Math.Max(w, h) > maxSide)
            {
                double k = maxSide / (double)Math.Max(w, h);
                w = Math.Max(1, (int)(w * k));
                h = Math.Max(1, (int)(h * k));
            }
            using var bmp = new System.Drawing.Bitmap(w, h);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                g.DrawImage(src, 0, 0, w, h);
            }
            using var outMs = new MemoryStream();
            bmp.Save(outMs, System.Drawing.Imaging.ImageFormat.Jpeg);
            return "data:image/jpeg;base64," + Convert.ToBase64String(outMs.ToArray());
        }
        catch { return null; }
    }

    private void ApplyCoverPreview(string dataUrl)
    {
        var bmp = DataUrlToScaledBitmap(dataUrl, 260);
        if (bmp != null)
        {
            CoverImage.Source = bmp;
            CoverImage.Visibility = Visibility.Visible;
            CoverPlaceholder.Visibility = Visibility.Collapsed;
        }
        _pendingCover = dataUrl;
        CoverHint.Text = "已选择新封面，保存后生效";
    }

    private void PickFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择封面图片",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif|所有文件|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;
        var dataUrl = FileToCoverDataUrl(dlg.FileName);
        if (dataUrl == null)
        {
            CoverHint.Text = "图片读取失败，请换一张";
            return;
        }
        ApplyCoverPreview(dataUrl);
    }

    private void GalleryThumb_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button b && b.Tag is GalleryThumbItem item)
        {
            var gallery = _db?.LoadGalleryItems(_original!.Id) ?? new List<(int, string)>();
            var hit = gallery.FirstOrDefault(g => g.Index == item.Index);
            if (hit.DataUrl != null)
            {
                var dataUrl = GalleryToCoverDataUrl(hit.DataUrl);
                if (dataUrl != null) ApplyCoverPreview(dataUrl);
            }
        }
    }

    /// <summary>删除一张 gallery 图片（确认后从 UI 移除并记录原始 index，保存时应用）</summary>
    private void GalleryRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button b && b.Tag is GalleryThumbItem item)
        {
            if (MessageBox.Show(this, "确定删除这张图片？\n（删除后在保存时生效）", "删除图片",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

            _removedGallery.Add(item.Index);

            // UI 移除
            if (GalleryPick.ItemsSource is List<GalleryThumbItem> list)
            {
                var hit = list.FirstOrDefault(x => x.Index == item.Index);
                if (hit != null)
                {
                    list.Remove(hit);
                    GalleryPick.ItemsSource = null;
                    GalleryPick.ItemsSource = list;
                }
                if (list.Count == 0)
                {
                    GalleryLabel.Visibility = Visibility.Collapsed;
                    GalleryPick.Visibility = Visibility.Collapsed;
                }
            }
        }
    }

    private void RemoveCover_Click(object sender, RoutedEventArgs e)
    {
        CoverImage.Source = null;
        CoverImage.Visibility = Visibility.Collapsed;
        CoverPlaceholder.Visibility = Visibility.Visible;
        CoverPlaceholder.Text = _original == null ? "无封面（可选）" : "无封面";
        _pendingCover = "__remove__";
        CoverHint.Text = "保存后将移除封面";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var title = TitleBox.Text.Trim();
        var content = ContentBox.Text.Trim();
        if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(content))
        {
            MessageBox.Show(this, "标题和内容至少填写一项。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var tags = TagsBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        Result = _original ?? new PromptItem { Id = DataService.NewId(), Type = "prompt", CreatedAt = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'") };
        Result.Title = title;
        Result.Content = content;
        Result.Tags = tags.Count > 0 ? tags : null;
        Result.Category = string.IsNullOrWhiteSpace(CategoryBox.Text) ? null : CategoryBox.Text.Trim();
        Result.Starred = StarredChk.IsChecked == true;

        // 应用封面变更与图片删除
        if (_db != null)
        {
            try
            {
                if (_pendingCover == "__remove__")
                {
                    _db.RemovePreviewImage(Result.Id);
                    Result.HasPreviewImage = false;
                }
                else if (_pendingCover != null)
                {
                    _db.SavePreviewImage(Result.Id, _pendingCover);
                    Result.HasPreviewImage = true;
                }
                if (_removedGallery.Count > 0)
                    _db.ApplyGalleryRemovals(Result.Id, _removedGallery);

                // 删除图片后卡片无图 → 询问是否同时删除提示词（仅编辑已有卡片且确实删了图）
                if (_original != null && (_removedGallery.Count > 0 || _pendingCover == "__remove__"))
                {
                    var stillPreview = _pendingCover == null
                        ? !string.IsNullOrEmpty(_db.LoadPreviewImage(Result.Id))
                        : _pendingCover != "__remove__";
                    var galleryLeft = _db.LoadGalleryItems(Result.Id).Count;
                    if (!stillPreview && galleryLeft == 0)
                    {
                        var r = MessageBox.Show(this,
                            "这张卡片已经没有任何图片了，是否同时删除这条提示词？",
                            "提示词卡片", MessageBoxButton.YesNo, MessageBoxImage.Question);
                        Result.ShouldDelete = r == MessageBoxResult.Yes;
                    }
                }
            }
            catch (Exception ex)
            {
                try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "pb_save_err.txt"), $"[{DateTime.Now:HH:mm:ss}] {ex}\n\n"); } catch { }
            }
        }
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }
        DragMove();
    }
}
