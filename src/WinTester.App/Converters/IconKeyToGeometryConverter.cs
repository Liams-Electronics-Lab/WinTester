using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WinTester.App.Converters;

// Resolves a StandaloneToolViewModel.IconKey string (e.g. "keyIcon", "lockIcon") to its matching Geometry
// resource in Resources/Icons.xaml, letting StandaloneToolTileControl bind its icon per-instance instead of
// hardcoding keyIcon for every Tab 2 tile.
public sealed class IconKeyToGeometryConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value as string;
        if (string.IsNullOrEmpty(key)) key = "keyIcon";

        return Application.Current.TryFindResource(key)
               ?? Application.Current.TryFindResource("keyIcon")
               ?? DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
