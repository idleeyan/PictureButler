using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace PictureButler;

/// <summary>bool → Visibility：true 显示 / false 折叠</summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

/// <summary>bool → Brush：true 金色（已星标）/ false 暗灰（未星标）</summary>
public class StarColorConverter : IValueConverter
{
    private static readonly SolidColorBrush Gold = new(Color.FromRgb(0xF5, 0xB8, 0x4B));
    private static readonly SolidColorBrush Dim = new(Color.FromRgb(0x4A, 0x51, 0x63));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Gold : Dim;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is SolidColorBrush b && b.Color == Gold.Color;
}
