using System;
using System.Globalization;
using System.Windows.Data;

namespace WinTester.App.Converters;

// Maps a live MB/s figure to 0-100 percent-of-scale for the same clip-to-reveal-gradient progress bars the
// run overlay uses (StorageBenchmarkControl's read/write bars reuse that mechanism rather than a second
// one). ConverterParameter sets the MB/s value treated as 100%; StorageBenchmarkViewModel supplies a fixed
// placeholder scale here, not a tuned ceiling.
public sealed class SpeedToPercentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double mbs) return 0.0;

        double maxScale = 3000.0;
        if (parameter is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            maxScale = parsed;

        if (maxScale <= 0) return 0.0;
        return Math.Clamp(mbs / maxScale * 100.0, 0.0, 100.0);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
