using System.Diagnostics;
using System.Windows;

namespace PictureButler;

/// <summary>
/// 主题管理：深色 / 浅色。
/// <para>
/// 两套主题是两份「键集合完全一致」的资源字典（Themes/Semantic.Dark.xaml 与 Semantic.Light.xaml），
/// 切换方式为替换 Application.Resources.MergedDictionaries 的语义槽位（红线 R10：
/// 禁止逐个 Brush 赋值，一定会漏）。
/// </para>
/// <para>
/// 为什么切换后要重启而不是即时刷新：WPF 的 StaticResource 只在元素构造时解析一次，
/// 换字典不会回溯刷新已存在的窗口。除非把所有颜色引用改成 DynamicResource（改动面太大且易漏），
/// 正确做法是「启动前选好字典」——这既零遗漏，也不违反 R10。
/// </para>
/// </summary>
public static class ThemeManager
{
    /// <summary>MergedDictionaries 中语义字典所在的固定槽位（0 = 原语层，1 = 语义层）</summary>
    private const int SemanticSlot = 1;

    public const string Dark = "dark";
    public const string Light = "light";

    /// <summary>命令行标记：由主题重启发起，用于让旧实例释放互斥量期间不误判「已有实例」</summary>
    public const string RelaunchArg = "--relaunch";

    public static bool IsDark(string? theme)
        => !string.Equals(theme, Light, StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string? theme) => IsDark(theme) ? Dark : Light;

    private static Uri SemanticUri(string theme)
        => new Uri($"pack://application:,,,/Themes/Semantic.{(IsDark(theme) ? "Dark" : "Light")}.xaml", UriKind.Absolute);

    /// <summary>
    /// 在「创建任何 Window 之前」应用主题。深色是 App.xaml 里的默认值，只有浅色需要替换槽位。
    /// </summary>
    public static void ApplyAtStartup(string? theme)
    {
        try
        {
            if (IsDark(theme)) return;
            var dicts = Application.Current?.Resources?.MergedDictionaries;
            if (dicts == null || dicts.Count <= SemanticSlot) return;
            dicts[SemanticSlot] = new ResourceDictionary { Source = SemanticUri(Light) };
        }
        catch { /* 主题失败不应阻止启动，回落到深色即可 */ }
    }

    /// <summary>
    /// 启动新的实例来完成主题切换。返回 true 表示已发起，调用方应立即 Shutdown 当前实例。
    /// </summary>
    public static bool LaunchRelaunch()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, ArgumentList = { RelaunchArg } });
            return true;
        }
        catch { return false; }
    }
}
