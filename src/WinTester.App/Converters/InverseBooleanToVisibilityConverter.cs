using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WinTester.App.Converters;

// True → Collapsed, false → Visible; drives the Start tile's "why it's disabled" caption.
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool b && b ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
