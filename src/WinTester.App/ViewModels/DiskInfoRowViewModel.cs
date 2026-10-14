using System.Windows;
using System.Windows.Media;
using WinTester.Tools.DiskInfo;

namespace WinTester.App.ViewModels;

// One line of the Disk Info table. Built once per report and never changed, so no change notification is needed.
// A flagged row (error or abnormal value) is drawn in bold red; everything else is plain.
public sealed class DiskInfoRowViewModel
{
    private static readonly Brush FlaggedBrush = Frozen(0xFF, 0x4D, 0x4D);
    private static readonly Brush NormalNameBrush = Frozen(0xB7, 0xB7, 0xBE);
    private static readonly Brush NormalValueBrush = Frozen(0xE1, 0xE3, 0xE8);
    private static readonly Brush DetailBrush = Frozen(0x7A, 0x7A, 0x82);

    public DiskInfoRowViewModel(SmartRow row)
    {
        Row = row;
    }

    public SmartRow Row { get; }

    public string Name => Row.Name;
    public string Value => Row.Value;
    public string Detail => Row.Detail ?? string.Empty;
    public bool HasDetail => !string.IsNullOrEmpty(Row.Detail);

    public Brush NameBrush => Row.Flagged ? FlaggedBrush : NormalNameBrush;
    public Brush ValueBrush => Row.Flagged ? FlaggedBrush : NormalValueBrush;
    public Brush DetailForeground => Row.Flagged ? FlaggedBrush : DetailBrush;
    public FontWeight Weight => Row.Flagged ? FontWeights.Bold : FontWeights.Normal;

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
