using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace WinTester.App.Converters;

// Together these two converters produce bold percent text over a progress bar where the portion sitting on
// the green fill reads black and the portion on the dark track reads white: an identical white and black
// TextBlock are stacked full-bar-width, then the black one is clipped to the fill's pixel width. Both take
// a MultiBinding of [percent (0-100), track ActualWidth] so the math stays correct as the bar resizes.
public sealed class PercentToPixelWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not double percent || values[1] is not double trackWidth)
            return 0.0;

        var clamped = Math.Clamp(percent, 0, 100);
        return trackWidth * (clamped / 100.0);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

// Produces the RectangleGeometry used as the black overlay TextBlock's Clip, so only the slice sitting over
// the green fill (0..fillWidth, full height) stays visible.
public sealed class PercentToClipRectConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 3 ||
            values[0] is not double percent ||
            values[1] is not double trackWidth ||
            values[2] is not double trackHeight)
        {
            return Geometry.Empty;
        }

        var clamped = Math.Clamp(percent, 0, 100);
        var fillWidth = trackWidth * (clamped / 100.0);
        if (fillWidth <= 0 || trackHeight <= 0) return Geometry.Empty;

        return new RectangleGeometry(new Rect(0, 0, fillWidth, trackHeight));
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
