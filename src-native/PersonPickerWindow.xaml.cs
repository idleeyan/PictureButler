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

/// <summary>选择合并目标人物（带搜索 + 选中态 + 异步头像加载）</summary>
public partial class PersonPickerWindow : Window
{
    private readonly List<PersonItem> _allPersons;
    private readonly string _imgTagDir;
    private PersonItem? _selected;
    private CancellationTokenSource? _avatarCts;

    public long SelectedPersonId => _selected?.Id ?? 0;

    public PersonPickerWindow(string sourceName, List<PersonItem> persons, string imgTagDir)
    {
        InitializeComponent();
        TitleText.Text = $"合并 · {sourceName}";
        _allPersons = persons;
        _imgTagDir = imgTagDir;
        // 先清掉选中态（可能从外部带进来）
        foreach (var p in _allPersons) p.IsSelected = false;
        PersonList.ItemsSource = _allPersons;
        Loaded += OnLoaded;
        KeyDown += PersonPickerWindow_KeyDown;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SearchBox.Focus();
        // 后台异步加载头像
        _avatarCts = new CancellationTokenSource();
        _ = LoadAvatarsAsync(_avatarCts.Token);
    }

    private async Task LoadAvatarsAsync(CancellationToken token)
    {
        try
        {
            // 第一阶段：直接从本地 .faces 缓存加载已有的头像
            var facesDir = Path.Combine(_imgTagDir, ".faces");
            var hasCache = Directory.Exists(facesDir);
            foreach (var p in _allPersons)
            {
                if (token.IsCancellationRequested) return;
                if (p.Thumb != null) continue;
                if (hasCache && p.AvatarFaceId.HasValue)
                {
                    var path = Path.Combine(facesDir, p.AvatarFaceId.Value + ".jpg");
                    if (File.Exists(path))
                    {
                        try
                        {
                            var bmp = new BitmapImage();
                            bmp.BeginInit();
                            bmp.CacheOption = BitmapCacheOption.OnLoad;
                            bmp.DecodePixelWidth = 80;
                            bmp.UriSource = new Uri(path);
                            bmp.EndInit();
                            bmp.Freeze();
                            await Dispatcher.InvokeAsync(() =>
                            {
                                if (!token.IsCancellationRequested) p.Thumb = bmp;
                            });
                        }
                        catch { }
                    }
                }
            }

            // 第二阶段：对还没有头像的，通过本地 HTTP 接口触发生成
            var missing = _allPersons.Where(p => p.Thumb == null && p.AvatarFaceId.HasValue).ToList();
            if (missing.Count == 0) return;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var sem = new SemaphoreSlim(4);
            await Task.WhenAll(missing.Select(async p =>
            {
                await sem.WaitAsync(token);
                try
                {
                    token.ThrowIfCancellationRequested();
                    var url = $"{ImgtagRecognizer.ServerUrl}/api/face-thumbnail/{p.AvatarFaceId}";
                    var bytes = await http.GetByteArrayAsync(url, token);
                    var bmp = new BitmapImage();
                    using var ms = new MemoryStream(bytes);
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = 80;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                    bmp.Freeze();
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (!token.IsCancellationRequested) p.Thumb = bmp;
                    });
                }
                catch { }
                finally { sem.Release(); }
            }));
        }
        catch { }
    }

    // ===== 搜索 =====

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var q = SearchBox.Text.Trim();
        IEnumerable<PersonItem> filtered = string.IsNullOrEmpty(q)
            ? _allPersons
            : _allPersons.Where(p => p.Name.Contains(q, StringComparison.OrdinalIgnoreCase));

        var list = filtered.ToList();
        // 如果当前选中的不在过滤结果里，清除选中
        if (_selected != null && !list.Any(p => p.Id == _selected.Id))
        {
            ClearSelection();
        }
        PersonList.ItemsSource = list;
        EmptyState.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down)
        {
            MoveSelection(1);
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            MoveSelection(-1);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            if (_selected != null) Ok_Click(sender, e);
            e.Handled = true;
        }
    }

    private void PersonPickerWindow_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Cancel_Click(sender, e);
            e.Handled = true;
        }
    }

    // ===== 选中 =====

    private void Person_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is PersonItem p)
        {
            SelectPerson(p);
            // 双击直接确认
            if (e.ClickCount >= 2)
            {
                Ok_Click(this, new RoutedEventArgs());
            }
        }
    }

    private void SelectPerson(PersonItem p)
    {
        if (_selected != null) _selected.IsSelected = false;
        p.IsSelected = true;
        _selected = p;
        OkBtn.IsEnabled = true;
        // 文字化提示当前选中，避免仅靠颜色区分
        SelHint.Text = $"已选：{p.Name}（{p.PhotoCountText}）";
        SelHint.Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush");
    }

    /// <summary>清除选中并复位提示</summary>
    private void ClearSelection()
    {
        if (_selected != null) _selected.IsSelected = false;
        _selected = null;
        OkBtn.IsEnabled = false;
        SelHint.Text = "未选择目标人物";
        SelHint.Foreground = (System.Windows.Media.Brush)FindResource("TextDimBrush");
    }

    private void MoveSelection(int delta)
    {
        var list = PersonList.ItemsSource as List<PersonItem>;
        if (list == null || list.Count == 0) return;
        int idx = _selected == null ? -1 : list.IndexOf(_selected);
        int next = idx + delta;
        if (next < 0) next = 0;
        if (next >= list.Count) next = list.Count - 1;
        SelectPerson(list[next]);
        // 滚动到可见
        var container = PersonList.ItemContainerGenerator.ContainerFromIndex(next) as FrameworkElement;
        container?.BringIntoView();
    }

    // ===== 按钮 =====

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _avatarCts?.Cancel();
        DialogResult = false;
        Close();
    }

    /// <summary>自绘标题栏拖动（无边框窗口）</summary>
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); } catch { }
    }
}
