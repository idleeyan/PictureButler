using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace PictureButler;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstance;
    private EventWaitHandle? _activateSignal;
    private Thread? _activateThread;

    /// <summary>
    /// 三类未处理异常的统一入口（0.62.6）。
    /// 没有它们，WPF 未处理异常是「无弹窗、无日志、进程直接消失」——用户只看到闪一下。
    /// </summary>
    private void InstallExceptionHooks()
    {
        DispatcherUnhandledException += OnDispatcherUnhandled;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandled;
        TaskScheduler.UnobservedTaskException += OnUnobservedTask;
        AppLogger.Info($"App started  pid={Environment.ProcessId}  ver={GetType().Assembly.GetName().Version}");
    }

    private void OnDispatcherUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // UI 线程异常：通常还能继续跑（某个操作失败），弹非模态错误窗，用户可选继续/退出
        AppLogger.Error("DispatcherUnhandledException", e.Exception);
        try
        {
            ErrorWindow.Show("程序出错了",
                $"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                $"版本：{GetType().Assembly.GetName().Version}\n\n" +
                e.Exception.ToString(),
                fatal: false);
        }
        catch { }
        e.Handled = true;
    }

    private void OnDomainUnhandled(object sender, UnhandledExceptionEventArgs e)
    {
        // 进程级：界面状态已不可信，只记日志并弹 fatal 窗（不给「继续」）
        var ex = e.ExceptionObject as Exception;
        AppLogger.Error($"UnhandledException  terminating={e.IsTerminating}", ex ?? new Exception(e.ExceptionObject?.ToString() ?? "(null)"));
        if (e.IsTerminating)
        {
            ErrorWindow.Show("程序即将退出",
                $"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                $"版本：{GetType().Assembly.GetName().Version}\n" +
                $"终止：{e.IsTerminating}\n\n" +
                (ex?.ToString() ?? e.ExceptionObject?.ToString() ?? "(unknown)"),
                fatal: true);
        }
    }

    private void OnUnobservedTask(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        // 后台 Task 的未观察异常：不打断 UI，只记日志
        AppLogger.Error("UnobservedTaskException", e.Exception);
        e.SetObserved();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // 钩子必须最先装：后面的任何崩溃都要能留下痕迹
        InstallExceptionHooks();

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
