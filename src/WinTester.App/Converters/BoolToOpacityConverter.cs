using System;
using System.Globalization;
using System.Windows.Data;

namespace WinTester.App.Converters;

// Full opacity when supported, 0.35 dim when not, bound per-tile to ITestModule.SupportsAdvancedSettings.
// Dims rather than hides via visibility so an unsupported gear stays visible-but-dim, giving an
// at-a-glance signal instead of making it vanish.
public sealed class BoolToOpacityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? 1.0 : 0.35;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
