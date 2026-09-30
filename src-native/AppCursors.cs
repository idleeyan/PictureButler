using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Resources;

namespace PictureButler;

/// <summary>
/// 专属鼠标样式 —— 0.62.0 起**只有一种：棱角箭头**。
/// 历史：曾配套一个手型光标，用户认为造型难看（0.61.0 那版三指阶梯在 32px 下挤成一团），
/// 且「只留箭头」与「删掉整项」之间用户选择了前者，因此可点击元素也统一用箭头，不再提供开关。
/// 资源嵌入 cursors/arrow.cur；经 Application 资源 CurArrow / CurHand 动态下发。
/// </summary>
public static class AppCursors
{
    public static Cursor Arrow { get; private set; } = Cursors.Arrow;

    /// <summary>与 Arrow 同源。手型光标已废弃，此处保留同名资源以免改动几十处 {DynamicResource CurHand}。</summary>
    public static Cursor Hand { get; private set; } = Cursors.Arrow;

    /// <summary>装载光标并写入 Application 资源供 {DynamicResource} 引用。</summary>
    public static void Apply()
    {
        Arrow = Load("cursors/arrow.cur") ?? Cursors.Arrow;
        Hand = Arrow;

        if (Application.Current == null) return;
        Application.Current.Resources["CurArrow"] = Arrow;
        Application.Current.Resources["CurHand"] = Hand;
        foreach (Window w in Application.Current.Windows)
            w.Cursor = Arrow;
    }

    private static Cursor? Load(string relative)
    {
        try
        {
            var uri = new Uri($"pack://application:,,,/{relative}", UriKind.Absolute);
            StreamResourceInfo? sri = Application.GetResourceStream(uri);
            if (sri?.Stream == null) return null;
            using var ms = new MemoryStream();
            sri.Stream.CopyTo(ms);
            ms.Position = 0;
            // stretch=false：按设计像素显示，避免被拉糊
            return new Cursor(ms, false);
        }
        catch { return null; }
    }
}
