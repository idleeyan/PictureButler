using System;
using System.Windows;
using System.Windows.Controls;

namespace PictureButler;

/// <summary>
/// 固定按可用宽度换行的面板。
/// 不用 IScrollInfo：交给外层 ScrollViewer 做物理滚动。
/// 关键：父级若给出无限宽（ScrollViewer 测量偶发），会退化成单行、纵向永远滚不动；
/// 这里强制用有限宽度换行，保证高度可以超出视口。
/// </summary>
public class FlowWrapPanel : Panel
{
    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(
        nameof(ItemWidth), typeof(double), typeof(FlowWrapPanel),
        new FrameworkPropertyMetadata(168.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(
        nameof(ItemHeight), typeof(double), typeof(FlowWrapPanel),
        new FrameworkPropertyMetadata(190.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double ItemWidth { get => (double)GetValue(ItemWidthProperty); set => SetValue(ItemWidthProperty, value); }
    public double ItemHeight { get => (double)GetValue(ItemHeightProperty); set => SetValue(ItemHeightProperty, value); }

    private const double FallbackWidth = 1100;

    private double ResolveWidth(Size availableSize)
    {
        if (!double.IsInfinity(availableSize.Width) && availableSize.Width > 0)
            return availableSize.Width;

        // 无限宽时向上找有限 ActualWidth
        DependencyObject? d = this;
        for (int i = 0; i < 6 && d != null; i++)
        {
            if (d is FrameworkElement fe && !double.IsInfinity(fe.ActualWidth) && fe.ActualWidth > 1)
                return fe.ActualWidth;
            try { d = System.Windows.Media.VisualTreeHelper.GetParent(d); }
            catch { d = LogicalTreeHelper.GetParent(d); }
        }
        return FallbackWidth;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = ResolveWidth(availableSize);
        int cols = Math.Max(1, (int)(width / Math.Max(1, ItemWidth)));
        int count = InternalChildren.Count;
        int rows = count == 0 ? 0 : (count + cols - 1) / cols;
        var childSize = new Size(ItemWidth, ItemHeight);
        foreach (UIElement child in InternalChildren) child.Measure(childSize);
        return new Size(width, rows * ItemHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double width = ResolveWidth(new Size(finalSize.Width, finalSize.Height));
        int cols = Math.Max(1, (int)(width / Math.Max(1, ItemWidth)));
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            int row = i / cols;
            int col = i % cols;
            InternalChildren[i].Arrange(new Rect(col * ItemWidth, row * ItemHeight, ItemWidth, ItemHeight));
        }
        return finalSize;
    }
}
