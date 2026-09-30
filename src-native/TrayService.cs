using System.Drawing;
using System.Windows.Forms;

namespace PictureButler;

/// <summary>
/// 托盘 + 全局热键：
/// - 关闭主窗口 → 隐藏到托盘（本地 HTTP 服务保持常驻，扩展才能连接）
/// - 托盘菜单：打开主界面 / 退出
/// - 全局热键 Ctrl+Alt+Space 呼出主窗口
/// </summary>
public class TrayService : IDisposable
{
    private NotifyIcon? _tray;
    private Action _showMain;
    private Action _quit;
    private bool _disposed;

    public TrayService(Action showMain, Action quit)
    {
        _showMain = showMain;
        _quit = quit;
    }

    public void Init()
    {
        _tray = new NotifyIcon();
        using var bmp = new System.Drawing.Bitmap(16, 16);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.Clear(System.Drawing.Color.FromArgb(0x1B, 0x1E, 0x28));
            g.FillEllipse(System.Drawing.Brushes.DodgerBlue, 2, 2, 12, 12);
            using var pen = new System.Drawing.Pen(System.Drawing.Color.White, 1.6f);
            g.DrawEllipse(pen, 4, 4, 8, 8);
        }
        _tray.Icon = System.Drawing.Icon.FromHandle(bmp.GetHicon());
        _tray.Text = "PictureButler 提示词管理器";
        _tray.Visible = true;
        _tray.DoubleClick += (_, _) => _showMain();

        var menu = new ContextMenuStrip();
        menu.Items.Add("打开主界面", null, (_, _) => _showMain());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出 PictureButler", null, (_, _) => _quit());
        _tray.ContextMenuStrip = menu;
    }

    /// <summary>托盘气泡通知（线程安全，任意线程可调用）</summary>
    public void Notify(string title, string text)
    {
        try
        {
            if (_tray == null) return;
            _tray.BalloonTipTitle = title;
            _tray.BalloonTipText = text;
            _tray.BalloonTipIcon = ToolTipIcon.Info;
            _tray.ShowBalloonTip(4000);
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }
    }
}
