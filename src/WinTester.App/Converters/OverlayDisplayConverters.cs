using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WinTester.App.Converters;

// True when the bound string equals ConverterParameter (case-insensitive); drives TestPatternControl.IsGpuActive
// off MainViewModel.CurrentTestId without a code-behind event handler.
public sealed class StringEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value is string s && parameter is string p && string.Equals(s, p, StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

// Empty/null string → Collapsed, anything else → Visible; hides the running-overlay's countdown/stage chips
// when a module hasn't reported one (e.g. no fixed duration to count down).
public sealed class StringEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

// Inverse of the above; Visible when the bound string IS null/empty, Collapsed otherwise. Used for
// StandaloneToolTileControl's vector-icon fallback: shows the old Icons.xaml Path only when a tile has no
// IconFileName PNG assigned, hides it once one is.
public sealed class StringEmptyToVisibilityInverseConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

// Visible when the bound string equals ConverterParameter (case-insensitive), Collapsed otherwise, same
// comparison as StringEqualsConverter, but yields Visibility directly since XAML can't chain a
// BooleanToVisibilityConverter after it within one Binding.
public sealed class StringEqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value is string s && parameter is string p && string.Equals(s, p, StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
