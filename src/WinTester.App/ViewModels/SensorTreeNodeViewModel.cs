using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace WinTester.App.ViewModels;

// One row in the Sensors tool's tree (SensorsWindow). Three kinds share this type: Source group
// ("LibreHardwareMonitor" / "HWiNFO"), hardware group (one per RawSensorRow.HardwareName), and leaf
// sensor reading.
//
// Mutable and INotifyPropertyChanged: SensorsWindow keeps the same node instances alive across ticks and
// updates their value fields in place, so a collapsed group stays collapsed instead of snapping back
// open every refresh. IsExpanded is genuinely two-way for the same reason.
//
// The tree does not rely on TreeViewItem's built-in per-level indent, since that would shift the whole
// row (including the value columns) rather than just the label. Instead every row gets the same
// fixed-width value columns, with indentation applied only to the label via Indent below.
public sealed class SensorTreeNodeViewModel : INotifyPropertyChanged
{
    public required string Label { get; init; }
    public required bool IsGroup { get; init; }

    // 0 = Source group, 1 = hardware group, 2 = leaf reading.
    public required int Depth { get; init; }
    public Thickness Indent => new(Depth * 16, 0, 4, 0);

    // Leaf rows only: colored accent bar keyed by reading category (Temperature/Voltage/Fan/...) — a
    // scan aid, not a status indicator.
    public Brush AccentBrush { get; init; } = Brushes.Transparent;

    // Leaf rows only: reading category tag shown dimmed next to the sensor name, e.g. "Temperature".
    public string CategoryTag { get; init; } = string.Empty;

    // Recomputed whenever IsExpanded changes so the glyph stays in sync automatically.
    public string ArrowGlyph => !IsGroup ? string.Empty : IsExpanded ? "\u25BE" : "\u25B8";

    private string _countText = string.Empty;
    public string CountText { get => _countText; set => Set(ref _countText, value); }

    private string _current = string.Empty;
    public string Current { get => _current; set => Set(ref _current, value); }

    private string _minimum = string.Empty;
    public string Minimum { get => _minimum; set => Set(ref _minimum, value); }

    private string _maximum = string.Empty;
    public string Maximum { get => _maximum; set => Set(ref _maximum, value); }

    // Running mean since this window opened, computed locally from per-tick Current values — neither
    // backend exposes an average (LHM/HWiNFO only provide Min/Max), so this resets if the window is
    // closed and reopened.
    private string _average = string.Empty;
    public string Average { get => _average; set => Set(ref _average, value); }

    private bool _isExpanded = true;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            OnChanged(nameof(IsExpanded));
            OnChanged(nameof(ArrowGlyph));
        }
    }

    public System.Collections.ObjectModel.ObservableCollection<SensorTreeNodeViewModel> Children { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        OnChanged(name);
    }

    private void OnChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
