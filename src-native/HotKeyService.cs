using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PictureButler;

/// <summary>全局热键：Ctrl+Alt+Space 呼出主窗口（Win32 RegisterHotKey）</summary>
public class HotKeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int MOD_CONTROL = 0x0002;
    private const int MOD_ALT = 0x0001;
    private const uint VK_SPACE = 0x20;
    private const int HOTKEY_ID = 0x1B01;

    private readonly Action _callback;
    private IntPtr _hwnd;
    private HwndSource? _source;
    private bool _disposed;

    public HotKeyService(Action callback) => _callback = callback;

    public void Register(Window window)
    {
        _hwnd = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
        RegisterHotKey(_hwnd, HOTKEY_ID, MOD_CONTROL | MOD_ALT, VK_SPACE);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
        {
            _callback();
            handled = true;
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hwnd != IntPtr.Zero)
        {
            UnregisterHotKey(_hwnd, HOTKEY_ID);
            _source?.RemoveHook(WndProc);
        }
    }
}
