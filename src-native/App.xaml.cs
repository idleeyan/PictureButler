using System.Threading;
using System.Windows;

namespace PictureButler;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstance;
    private EventWaitHandle? _activateSignal;
    private Thread? _activateThread;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 主题必须在 MainWindow 解析前选好字典：StaticResource 只在元素构造时解析一次
        var settings = AppSettings.Load();
        AppCursors.Apply();
        ThemeManager.ApplyAtStartup(settings.Theme);

        bool relaunch = Array.IndexOf(e.Args, ThemeManager.RelaunchArg) >= 0;

        _singleInstance = new Mutex(true, @"Local\PictureButler_Native_Single", out bool createdNew);

        // 切主题时的重启：新进程先起来、旧进程还没退出，互斥量短暂被占。
        // 带 --relaunch 的实例会重试等待（最多约 4 秒），避免误判成「已有实例」而双双退出。
        if (!createdNew && relaunch)
        {
            for (int i = 0; i < 20 && !createdNew; i++)
            {
                Thread.Sleep(200);
                _singleInstance.Dispose();
                _singleInstance = new Mutex(true, @"Local\PictureButler_Native_Single", out createdNew);
            }
        }

        if (!createdNew)
        {
            // 已有实例：通知它呼出主窗口，本进程直接退出（绝不新开第二个 UI）
            try
            {
                using var sig = EventWaitHandle.OpenExisting("Local\\PictureButler_Activate");
                sig.Set();
            }
            catch { }
            try { NativeMethods.ShowExisting(); } catch { }
            Shutdown();
            return;
        }

        // 本实例负责接收「二次启动」信号
        try
        {
            _activateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\PictureButler_Activate");
            _activateThread = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        _activateSignal.WaitOne();
                    }
                    catch { break; }
                    try
                    {
                        Dispatcher.Invoke(() =>
                        {
                            if (MainWindow is MainWindow mw) mw.ShowFromTrayOrRestore();
                            else NativeMethods.ShowExisting();
                        });
                    }
                    catch { }
                }
            })
            { IsBackground = true };
            _activateThread.Start();
        }
        catch { }

        base.OnStartup(e);

        // 不用 StartupUri：否则二次启动在 Shutdown 后仍可能弹出主窗口
        var win = new MainWindow();
        MainWindow = win;
        win.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _activateSignal?.Dispose(); } catch { }
        try { _singleInstance?.Dispose(); } catch { }
        base.OnExit(e);
    }
}

internal static class NativeMethods
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    internal static extern uint SystemParametersInfo(uint uiAction, uint uiParam, string pvParam, uint fWinIni);

    /// <summary>找到已在运行的 PictureButler 主窗口并呼出（含托盘隐藏 / 最小化）</summary>
    public static void ShowExisting()
    {
        var h = FindWindow(null, "PictureButler");
        if (h == IntPtr.Zero) return;
        ShowWindow(h, 8); // SW_SHOWNA 先让隐藏窗口可见
        if (IsIconic(h)) ShowWindow(h, 9); // SW_RESTORE
        else ShowWindow(h, 5); // SW_SHOW
        SetForegroundWindow(h);
    }
}
