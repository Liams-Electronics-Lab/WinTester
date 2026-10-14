using System;
using System.Windows.Media;

namespace WinTester.App.ViewModels;

// Colours shared by the Hardware Errors list and its detail popup. All frozen so they are safe to share
// across rows and windows.
public static class HardwareErrorBrushes
{
    // Severity accent: red for fatal, orange for recoverable, amber for corrected, blue for informational, grey
    // for anything nothing classified.
    public static readonly Brush Fatal = Frozen(0xD9, 0x3B, 0x3B);
    public static readonly Brush Corrected = Frozen(0xE0, 0xA0, 0x30);
    public static readonly Brush Recoverable = Frozen(0xE0, 0x6A, 0x30);
    public static readonly Brush Informational = Frozen(0x4D, 0x9B, 0xFF);
    public static readonly Brush Other = Frozen(0x7A, 0x7A, 0x82);
    public static readonly Brush Clean = Frozen(0x3C, 0xB0, 0x43);

    // Event subheading: the event number is green and the level is coloured by how serious it is. Every
    // other part of the line stays the grey of the surrounding text.
    public static readonly Brush EventId = Frozen(0x3C, 0xB0, 0x43);
    public static readonly Brush LevelInformation = Frozen(0x4D, 0x9B, 0xFF);
    public static readonly Brush LevelWarning = Frozen(0xF2, 0xD0, 0x3C);
    public static readonly Brush LevelError = Frozen(0xE5, 0x48, 0x4D);

    // Critical outranks Error, so it shares the red. Verbose, "Unknown" and any level Windows named
    // something else get the neutral grey.
    public static Brush ForLevel(string level)
    {
        if (level.Equals("Information", StringComparison.OrdinalIgnoreCase)) return LevelInformation;
        if (level.Equals("Warning", StringComparison.OrdinalIgnoreCase)) return LevelWarning;
        if (level.Equals("Error", StringComparison.OrdinalIgnoreCase) ||
            level.Equals("Critical", StringComparison.OrdinalIgnoreCase)) return LevelError;

        return Other;
    }

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
