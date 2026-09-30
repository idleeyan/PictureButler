using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace PictureButler;

/// <summary>
/// 「建议合并」窗口：按人脸相似度列出可能属于同一个人的人物对，
/// 勾选后可一键把右侧人物并入左侧人物。
/// </summary>
public partial class MergeSuggestWindow : Window
{
    private readonly List<MergeSuggestion> _items;
    private readonly string _imgTagDir;
    private readonly ImageLibraryService _img;
    private CancellationTokenSource? _cts;

    /// <summary>实际执行的合并组数（窗口关闭后读取）</summary>
    public int MergedPairs { get; private set; }

    public MergeSuggestWindow(List<MergeSuggestion> items, string imgTagDir, ImageLibraryService img)
    {
        InitializeComponent();
        _items = items;
        _imgTagDir = imgTagDir;
        _img = img;
        SuggestList.ItemsSource = _items;

        bool any = _items.Count > 0;
        EmptyState.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        SuggestList.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        if (!any)
            HintText.Text = "没有找到相似度较高的人物，说明当前聚类结果已经比较干净。";

        Loaded += OnLoaded;
        KeyDown += Window_KeyDown;
        UpdateSelCount();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _cts = new CancellationTokenSource();
        _ = LoadThumbsAsync(_cts.Token);
    }

    /// <summary>两阶段加载头像：先读本地 .faces 缓存，再走本地 HTTP 触发生成</summary>
    private async Task LoadThumbsAsync(CancellationToken token)
    {
        try
        {
            var facesDir = Path.Combine(_imgTagDir, ".faces");
            var hasCache = Directory.Exists(facesDir);
            foreach (var it in _items)
            {
                if (token.IsCancellationRequested) return;
                if (hasCache)
                {
                    it.ThumbA ??= TryLoadLocal(facesDir, it.AvatarFaceIdA);
                    it.ThumbB ??= TryLoadLocal(facesDir, it.AvatarFaceIdB);
                }
            }

            var missing = new List<(MergeSuggestion Item, bool IsA)>();
            foreach (var it in _items)
            {
                if (it.ThumbA == null && it.AvatarFaceIdA.HasValue) missing.Add((it, true));
                if (it.ThumbB == null && it.AvatarFaceIdB.HasValue) missing.Add((it, false));
            }
            if (missing.Count == 0) return;

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var sem = new SemaphoreSlim(4);
            await Task.WhenAll(missing.Select(async m =>
            {
                await sem.WaitAsync(token);
                try
                {
                    token.ThrowIfCancellationRequested();
                    var fid = m.IsA ? m.Item.AvatarFaceIdA!.Value : m.Item.AvatarFaceIdB!.Value;
                    var bytes = await http.GetByteArrayAsync($"{ImgtagRecognizer.ServerUrl}/api/face-thumbnail/{fid}", token);
                    var bmp = new BitmapImage();
                    using var ms = new MemoryStream(bytes);
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = 72;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                    bmp.Freeze();
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (token.IsCancellationRequested) return;
                        if (m.IsA) m.Item.ThumbA = bmp; else m.Item.ThumbB = bmp;
                    });
                }
                catch { }
                finally { sem.Release(); }
            }));
        }
        catch { }
    }

    private static System.Windows.Media.ImageSource? TryLoadLocal(string facesDir, long? faceId)
    {
        if (!faceId.HasValue) return null;
        var path = Path.Combine(facesDir, faceId.Value + ".jpg");
        if (!File.Exists(path)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 72;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    // ===== 勾选 =====

    private void Row_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is MergeSuggestion s)
        {
            s.IsChecked = !s.IsChecked;
            UpdateSelCount();
            e.Handled = true;
        }
    }

    private void Chk_Click(object sender, RoutedEventArgs e) => UpdateSelCount();

    private void SelAll_Click(object sender, RoutedEventArgs e)
    {
        bool allChecked = _items.Count > 0 && _items.All(x => x.IsChecked);
        foreach (var s in _items) s.IsChecked = !allChecked;
        SelAllBtn.Content = allChecked ? "全选" : "取消全选";
        UpdateSelCount();
    }

    private void UpdateSelCount()
    {
        int n = _items.Count(x => x.IsChecked);
        SelCountText.Text = $"已选 {n} 组";
        OkBtn.IsEnabled = n > 0;
    }

    // ===== 执行合并 =====

    private async void Ok_Click(object sender, RoutedEventArgs e)
    {
        var picked = _items.Where(x => x.IsChecked).OrderByDescending(x => x.Similarity).ToList();
        if (picked.Count == 0) return;

        var preview = string.Join("\n", picked.Take(5).Select(p => "· " + p.MergeText));
        if (picked.Count > 5) preview += $"\n… 等 {picked.Count} 组";
        var confirm = MessageBox.Show(this,
            $"将执行以下合并（右侧人物并入左侧人物）：\n\n{preview}\n\n" +
            "合并后人物照片与封面会并入目标人物，原条目随即移除。是否继续？",
            "确认合并", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        OkBtn.IsEnabled = false;
        SelAllBtn.IsEnabled = false;

        int done = 0;
        await Task.Run(() =>
        {
            // 按相似度从高到低执行；已被并入的人物不再参与后续合并（避免链式失效）
            var gone = new HashSet<long>();
            foreach (var s in picked)
            {
                if (gone.Contains(s.PersonIdA) || gone.Contains(s.PersonIdB)) continue;
                if (_img.MergePersons(new List<long> { s.PersonIdB }, s.PersonIdA))
                {
                    gone.Add(s.PersonIdB);
                    done++;
                }
            }
        });

        MergedPairs = done;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        DialogResult = false;
        Close();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Cancel_Click(sender, e); e.Handled = true; }
    }

    /// <summary>自绘标题栏拖动（无边框窗口）</summary>
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); } catch { }
    }
}
