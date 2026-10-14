using WinTester.Core;

namespace WinTester.App.ViewModels;

// Formatting wrapper around RawSensorRow: pre-formats Value/Min/Max with the reading's unit and the
// app's "N/A" convention for a null reading. Immutable.
public sealed class SensorRowViewModel
{
    public required string Source { get; init; }       // "LibreHardwareMonitor" or "HWiNFO"
    public required string Category { get; init; }
    public required string HardwareName { get; init; }
    public required string SensorName { get; init; }
    public required string Value { get; init; }
    public required string Min { get; init; }
    public required string Max { get; init; }

    public static SensorRowViewModel From(RawSensorRow row) => new()
    {
        Source = row.Source,
        Category = row.Category,
        HardwareName = row.HardwareName,
        SensorName = row.SensorName,
        Value = Format(row.Value, row.Unit),
        Min = Format(row.Min, row.Unit),
        Max = Format(row.Max, row.Unit)
    };

    private static string Format(double? value, string unit) =>
        value is { } v ? $"{v:0.##} {unit}".TrimEnd() : "N/A";
}
