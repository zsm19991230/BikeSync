using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using BikeSync.App.ViewModels;

namespace BikeSync.App.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (parameter is string text && text.Equals("Invert", StringComparison.OrdinalIgnoreCase))
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is not true;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var visible = value is not null;
        if (parameter is string text && text.Equals("Invert", StringComparison.OrdinalIgnoreCase))
        {
            visible = !visible;
        }

        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var visible = value is string text && !string.IsNullOrWhiteSpace(text);
        if (parameter is string mode && mode.Equals("Empty", StringComparison.OrdinalIgnoreCase))
        {
            visible = !visible;
        }

        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var count = value switch
        {
            int i => i,
            long l => (int)l,
            _ => 0,
        };

        var visible = count > 0;
        if (parameter is string mode && mode.Equals("Empty", StringComparison.OrdinalIgnoreCase))
        {
            visible = count == 0;
        }

        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class HexToBrushConverter : IValueConverter
{
    private static readonly Dictionary<string, Brush> Cache = new(StringComparer.OrdinalIgnoreCase);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string hex || string.IsNullOrWhiteSpace(hex))
        {
            return Brushes.Gray;
        }

        lock (Cache)
        {
            if (Cache.TryGetValue(hex, out var cached))
            {
                return cached;
            }

            try
            {
                var color = (Color)ColorConverter.ConvertFromString(hex);
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                Cache[hex] = brush;
                return brush;
            }
            catch (FormatException)
            {
                return Brushes.Gray;
            }
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class SyncStateBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value switch
        {
            SyncRowState.Success => "SuccessBrush",
            SyncRowState.Duplicate => "WarningBrush",
            SyncRowState.Failed => "DangerBrush",
            SyncRowState.Running => "AccentBrush",
            _ => "TextMutedBrush",
        };
        return Application.Current.TryFindResource(key) ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class SyncStateSymbolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value switch
        {
            SyncRowState.Success => Wpf.Ui.Controls.SymbolRegular.CheckmarkCircle24,
            SyncRowState.Duplicate => Wpf.Ui.Controls.SymbolRegular.ArrowSyncCircle24,
            SyncRowState.Failed => Wpf.Ui.Controls.SymbolRegular.ErrorCircle24,
            SyncRowState.Running => Wpf.Ui.Controls.SymbolRegular.ArrowSync24,
            _ => Wpf.Ui.Controls.SymbolRegular.Clock24,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class NavSelectedBackgroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true
            ? Application.Current.TryFindResource("NavSelectedBrush") ?? Brushes.DimGray
            : Brushes.Transparent;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
