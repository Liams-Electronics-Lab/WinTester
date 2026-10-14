using System.ComponentModel;
using System.Runtime.CompilerServices;
using WinTester.Tests.Storage;

namespace WinTester.App.ViewModels;

// One cell of the storage grid overlay. Backs one PhysicalDiskInfo's UI: the drive info line, all four
// speed bars (Seq/Rand Write/Read, one per FioProfile×phase StorageStressTestModule runs), and the
// pulsing-red/solid-green border state.
public sealed class StorageDriveCellViewModel : INotifyPropertyChanged
{
    // MB/s the Seq1M Q8T1 bars treat as 100%/full-green; a fixed, unscaled ceiling well above real NVMe
    // Gen4 sequential speeds, so bar color means the same thing across every disk in a run. Untuned
    // placeholder. Not actually read by StorageBenchmarkControl.xaml; SpeedToPercentConverter's
    // ConverterParameter must be a XAML-literal string ("3000"), which can't bind to this constant; so
    // this is the source-of-truth rationale only; changing it needs a matching manual edit to that "3000".
    public const double SeqMaxScaleMBs = 3000.0;

    // Same idea as SeqMaxScaleMBs but for Rand4K Q1T1 bars, whose throughput (tens to a few hundred MB/s
    // even on fast NVMe) is far below Seq1M Q8T1's range; reusing SeqMaxScaleMBs would leave Rand bars
    // permanently near-empty. Untuned placeholder; same XAML-string caveat, with a "300" literal there.
    public const double RandMaxScaleMBs = 300.0;

    // Stable key for this cell; the drive letter StorageStressTestModule's events are keyed by. Not bound
    // in XAML (DriveInfoText is displayed instead); MainViewModel uses it to route incoming events.
    public string DriveLetter { get; }

    public StorageDriveCellViewModel(string driveLetter)
    {
        DriveLetter = driveLetter;
    }

    private string _driveInfoText = "";
    // "{DriveLetter}: {Model} ({FreeGB:0.00}GB Free)": each grid cell's header line.
    public string DriveInfoText
    {
        get => _driveInfoText;
        set { if (_driveInfoText == value) return; _driveInfoText = value; OnPropertyChanged(); }
    }

    private DriveTestState _state = DriveTestState.Pending;
    // Drives the cell border: IsTesting pulses red, IsComplete is solid green, Pending stays at the
    // style's default resting border.
    public DriveTestState State
    {
        get => _state;
        set
        {
            if (_state == value) return;
            _state = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsTesting));
            OnPropertyChanged(nameof(IsComplete));
        }
    }

    public bool IsTesting => State == DriveTestState.Testing;
    public bool IsComplete => State == DriveTestState.Complete;

    private double? _tempC;
    // Live drive temperature, updated from StorageStressTestModule.DriveTelemetryChanged via
    // StorageBenchmarkViewModel.ApplyTelemetry. Null means no reading yet or no sensor found for this
    // disk; TempHealthText renders that as "N/A" rather than a misleading "0°C".
    public double? TempC
    {
        get => _tempC;
        set
        {
            if (_tempC == value) return;
            _tempC = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TempHealthText));
        }
    }

    private double? _healthPercent;
    // Live SMART/remaining-life health percentage. Null means no reading yet or no health sensor exposed
    // (common on older SATA SSDs and most HDDs); not "0% healthy".
    public double? HealthPercent
    {
        get => _healthPercent;
        set
        {
            if (_healthPercent == value) return;
            _healthPercent = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TempHealthText));
        }
    }

    // Single combined line ["Temp: 41°C · Health: 98%"] instead of two TextBlocks, to keep the cell
    // header compact alongside the drive info line and four speed bars. Each half falls back to "N/A"
    // independently rather than hiding the whole line.
    public string TempHealthText =>
        $"Temp: {(TempC is { } t ? t.ToString("0") + "°C" : "N/A")} · " +
        $"Health: {(HealthPercent is { } h ? h.ToString("0") + "%" : "N/A")}";

    private double? _seqWriteSpeedMBs;
    public double? SeqWriteSpeedMBs
    {
        get => _seqWriteSpeedMBs;
        set
        {
            if (_seqWriteSpeedMBs == value) return;
            _seqWriteSpeedMBs = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SeqWriteSpeedText));
            OnPropertyChanged(nameof(SeqWriteBarPercentInput));
        }
    }

    private double? _seqReadSpeedMBs;
    public double? SeqReadSpeedMBs
    {
        get => _seqReadSpeedMBs;
        set
        {
            if (_seqReadSpeedMBs == value) return;
            _seqReadSpeedMBs = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SeqReadSpeedText));
            OnPropertyChanged(nameof(SeqReadBarPercentInput));
        }
    }

    private string? _randSkipReason;
    // Phase 3.4: non-null when StorageStressTestModule's quick-pass gating decided to skip Rand4K Q1T1
    // entirely for this disk (mechanical HDD, or an undetermined/Unknown drive type treated the same way,
    // and the operator hasn't overridden it in Advanced Mode). RandWriteSpeedText/RandReadSpeedText show
    // this instead of the usual "--"/live MB/s, so the bars read as "skipped, and here's why" rather than
    // "stuck at rest" to an operator watching the grid.
    public string? RandSkipReason
    {
        get => _randSkipReason;
        set
        {
            if (_randSkipReason == value) return;
            _randSkipReason = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RandWriteSpeedText));
            OnPropertyChanged(nameof(RandReadSpeedText));
        }
    }

    private double? _randWriteSpeedMBs;
    public double? RandWriteSpeedMBs
    {
        get => _randWriteSpeedMBs;
        set
        {
            if (_randWriteSpeedMBs == value) return;
            _randWriteSpeedMBs = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RandWriteSpeedText));
            OnPropertyChanged(nameof(RandWriteBarPercentInput));
        }
    }

    private double? _randReadSpeedMBs;
    public double? RandReadSpeedMBs
    {
        get => _randReadSpeedMBs;
        set
        {
            if (_randReadSpeedMBs == value) return;
            _randReadSpeedMBs = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RandReadSpeedText));
            OnPropertyChanged(nameof(RandReadBarPercentInput));
        }
    }

    // "--" before the first sample of a phase rather than a misleading "0.0", per
    // StorageLiveEventArgs.SpeedMBs's own null-means-no-sample-yet contract.
    public string SeqWriteSpeedText => $"SEQ WRITE: {(SeqWriteSpeedMBs is { } v ? v.ToString("0.0") : "--")} MB/s";
    public string SeqReadSpeedText => $"SEQ READ: {(SeqReadSpeedMBs is { } v ? v.ToString("0.0") : "--")} MB/s";
    public string RandWriteSpeedText => RandSkipReason is { } skipW
        ? $"RAND WRITE: skipped ({skipW})"
        : $"RAND WRITE: {(RandWriteSpeedMBs is { } v ? v.ToString("0.0") : "--")} MB/s";
    public string RandReadSpeedText => RandSkipReason is { } skipR
        ? $"RAND READ: skipped ({skipR})"
        : $"RAND READ: {(RandReadSpeedMBs is { } v ? v.ToString("0.0") : "--")} MB/s";

    // SpeedToPercentConverter's input value; 0 (empty bar) until the first live sample arrives, same
    // "at rest" treatment the *SpeedText properties' "--" gives the text.
    public double SeqWriteBarPercentInput => SeqWriteSpeedMBs ?? 0.0;
    public double SeqReadBarPercentInput => SeqReadSpeedMBs ?? 0.0;
    public double RandWriteBarPercentInput => RandWriteSpeedMBs ?? 0.0;
    public double RandReadBarPercentInput => RandReadSpeedMBs ?? 0.0;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
