using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PictureButler;

/// <summary>
/// 崩溃/未处理异常的非模态错误窗。全文可复制，带「继续 / 退出」。
/// 刻意不用 MessageBox（模态会把非关键失败升级成强制中断）。
/// 刻意不依赖主题资源 —— 崩溃时字典可能已损坏，必须能独立画出来。
/// </summary>
public class ErrorWindow : Window
{
    private static Brush Res(string key, Brush fallback)
    {
        try { if (Application.Current?.TryFindResource(key) is Brush b) return b; } catch { }
        return fallback;
    }

    private ErrorWindow(string title, string detail, bool fatal)
    {
        Title = title;
        Width = 560;
        Height = 420;
        MinWidth = 420;
        MinHeight = 280;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.CanResize;
        Background = Res("BgBrush", SystemColors.WindowBrush);

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var head = new TextBlock
        {
            Text = fatal
                ? "程序遇到了无法继续的错误。可以先复制下面的信息，再点「退出」。"
                : "程序遇到了一个错误，但可以继续使用。建议复制下面的信息备查。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Res("TextBrush", SystemColors.ControlTextBrush),
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 10)
        };
        Grid.SetRow(head, 0);
        root.Children.Add(head);

        var box = new TextBox
        {
            Text = detail,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas, Cascadia Mono, Microsoft YaHei"),
            FontSize = 12,
            Padding = new Thickness(8),
            Background = Res("CardBrush", SystemColors.ControlBrush),
            Foreground = Res("TextBrush", SystemColors.ControlTextBrush),
            BorderBrush = Res("BorderBrush", SystemColors.ControlDarkBrush)
        };
        Grid.SetRow(box, 1);
        root.Children.Add(box);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };

        var copyBtn = new Button { Content = "复制全文", Width = 96, Height = 32, Margin = new Thickness(0, 0, 8, 0) };
        copyBtn.Click += (_, _) =>
        {
            try { Clipboard.SetText(detail); copyBtn.Content = "已复制"; }
            catch { copyBtn.Content = "复制失败"; }
        };
        buttons.Children.Add(copyBtn);

        if (!fatal)
        {
            var contBtn = new Button { Content = "继续", Width = 96, Height = 32, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            contBtn.Click += (_, _) => Close();
            buttons.Children.Add(contBtn);
        }

        var exitBtn = new Button { Content = "退出", Width = 96, Height = 32, IsCancel = true };
        exitBtn.Click += (_, _) =>
        {
            try { Application.Current.Shutdown(); } catch { Environment.Exit(1); }
        };
        buttons.Children.Add(exitBtn);

        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);
        Content = root;
    }

    /// <summary>非模态弹出；fatal=true 时不给「继续」（进程级异常，界面已不可信）。</summary>
    public static void Show(string title, string detail, bool fatal = false)
    {
        try
        {
            var win = new ErrorWindow(title, detail, fatal);
            win.Show();
            win.Activate();
        }
        catch
        {
            try
            {
                Directory.CreateDirectory(AppLogger.LogDir);
                File.AppendAllText(
                    Path.Combine(AppLogger.LogDir, "pb_crash_fallback.txt"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {title}\n{detail}\n\n");
            }
            catch { }
        }
    }
}
