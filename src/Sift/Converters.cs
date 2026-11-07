using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Sift.Core;
using Sift.Core.Finders;

namespace Sift;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool b = value is bool v && v;
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        bool has = value is not null && !(value is string s && s.Length == 0);
        if (Invert) has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Compares a value against the ConverterParameter (enum name). Two-way for RadioButton.IsChecked.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool eq = value is not null && parameter is not null && string.Equals(value.ToString(), parameter.ToString(), StringComparison.Ordinal);
        if (targetType == typeof(Visibility)) return eq ? Visibility.Visible : Visibility.Collapsed;
        return eq;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is true && parameter is not null)
        {
            if (targetType.IsEnum) return Enum.Parse(targetType, parameter.ToString()!);
            return parameter;
        }
        return Binding.DoNothing;
    }
}

public sealed class BytesConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is long l ? Format.Bytes(l) : value is int i ? Format.Bytes(i) : "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class RiskToBrushConverter : IValueConverter
{
    public bool Soft { get; set; }
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value switch
        {
            Risk.Safe => "Success",
            Risk.Caution => "Danger",
            _ => "Warning",
        };
        return Application.Current.TryFindResource(key + (Soft ? "SoftBrush" : "Brush"));
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class PercentToWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not double pct || values[1] is not double width) return 0d;
        return Math.Max(0, Math.Min(width, width * pct / 100.0));
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class StatusToBrushConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        ViewModels.ReviewStatus.Done or ViewModels.ReviewStatus.Handed => Application.Current.TryFindResource("SuccessBrush"),
        ViewModels.ReviewStatus.Failed => Application.Current.TryFindResource("DangerBrush"),
        _ => Application.Current.TryFindResource("TextDimBrush"),
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class CountToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool any = value is int i ? i > 0 : value is long l && l > 0;
        if (Invert) any = !any;
        return any ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class NotConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}
