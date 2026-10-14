using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using WinTester.Tests.Storage;

namespace WinTester.App.ViewModels;

// Backs StorageBenchmarkControl's running-state visual: a plain custom-drawn WPF grid rather than a
// reparented render window, since fio has no window of its own to embed. Thin shell: Drives holds one
// StorageDriveCellViewModel per disk, each with four bars (Seq/Rand Write/Read) and a pulsing-red/
// solid-green border for that disk's state; GridColumns/GridRows size a UniformGrid per ComputeGridDimensions.
public sealed class StorageBenchmarkViewModel : INotifyPropertyChanged
{
    private readonly Dictionary<string, StorageDriveCellViewModel> _cellsByDriveLetter = new();

    public ObservableCollection<StorageDriveCellViewModel> Drives { get; } = new();

    private int _gridColumns = 1;
    public int GridColumns
    {
        get => _gridColumns;
        set { if (_gridColumns == value) return; _gridColumns = value; OnPropertyChanged(); }
    }

    private int _gridRows = 1;
    public int GridRows
    {
        get => _gridRows;
        set { if (_gridRows == value) return; _gridRows = value; OnPropertyChanged(); }
    }

    // Rebuilds Drives from scratch and recomputes grid dimensions. Called once per run from
    // StorageStressTestModule.DisksAnnounced, which fires once with the full disk list (each paired with
    // its Phase 3 quick-pass decision) before any pass starts, so the grid renders complete from the first
    // frame instead of growing cell-by-cell.
    public void Reset(IReadOnlyList<AnnouncedStorageDisk> disks)
    {
        Drives.Clear();
        _cellsByDriveLetter.Clear();

        foreach (var announced in disks)
        {
            var disk = announced.Disk;
            var freeGb = disk.FreeBytes / 1024.0 / 1024.0 / 1024.0;
            var cell = new StorageDriveCellViewModel(disk.DriveLetter)
            {
                DriveInfoText = $"{disk.DriveLetter} {disk.Model} ({freeGb:0.00}GB Free)",
                RandSkipReason = announced.RandSkipReason
            };
            Drives.Add(cell);
            _cellsByDriveLetter[disk.DriveLetter] = cell;
        }

        var (cols, rows) = ComputeGridDimensions(disks.Count);
        GridColumns = cols;
        GridRows = rows;
    }

    // Routes a StorageStressTestModule.LiveSpeedChanged event to the right cell and bar via Profile/Phase.
    // No-ops if the drive letter isn't a known cell; shouldn't happen since Reset announces every disk
    // first, but a silent no-op beats throwing out of an event handler over an elsewhere-caused bug.
    public void ApplyEvent(StorageLiveEventArgs e)
    {
        if (!_cellsByDriveLetter.TryGetValue(e.DriveLetter, out var cell)) return;

        var freeGb = e.FreeBytes / 1024.0 / 1024.0 / 1024.0;
        cell.DriveInfoText = $"{e.DriveLetter} {e.Model} ({freeGb:0.00}GB Free)";
        cell.State = e.State;

        if (e.SpeedMBs is null && e.State != DriveTestState.Testing)
            return; // Pending/Complete announce events carry no bar update, only info text + border state.

        switch (e.Profile, e.Phase)
        {
            case (FioProfile.Seq1MQ8T1, StorageBenchmarkPhase.Write):
                cell.SeqWriteSpeedMBs = e.SpeedMBs;
                break;
            case (FioProfile.Seq1MQ8T1, StorageBenchmarkPhase.Read):
                cell.SeqReadSpeedMBs = e.SpeedMBs;
                break;
            case (FioProfile.Rand4KQ1T1, StorageBenchmarkPhase.Write):
                cell.RandWriteSpeedMBs = e.SpeedMBs;
                break;
            case (FioProfile.Rand4KQ1T1, StorageBenchmarkPhase.Read):
                cell.RandReadSpeedMBs = e.SpeedMBs;
                break;
        }
    }

    // Routes a StorageStressTestModule.DriveTelemetryChanged event (live drive temp/SMART health) to the
    // right cell via the same drive-letter lookup ApplyEvent uses; same no-op-if-unknown reasoning.
    public void ApplyTelemetry(StorageDriveTelemetryEventArgs e)
    {
        if (!_cellsByDriveLetter.TryGetValue(e.DriveLetter, out var cell)) return;

        cell.TempC = e.TempC;
        cell.HealthPercent = e.HealthPercent;
    }

    // Grid sizing per spec: 2 drives→1×2 (center-justified), 4→2×2, 6→3×3, 8→4×4; odd counts round up to
    // the next even grid with a trailing blank cell (e.g. 5→3×3, one blank). Formula generalizing those
    // points: round the count up to even, halve for side, grid is side×side, except side==1 special-cased
    // to 1×2 since a 1×1 grid can't hold 2 drives. A denser ceil(sqrt(N))² scheme was rejected since it
    // disagrees with the 6→3×3/8→4×4 examples; the resulting sparseness at higher drive counts (half the
    // grid blank at 8) is a known quirk worth revisiting if a shop machine ever needs 6+ drives.
    internal static (int Columns, int Rows) ComputeGridDimensions(int driveCount)
    {
        if (driveCount <= 0) return (1, 1);

        int roundedUp = driveCount % 2 == 0 ? driveCount : driveCount + 1;
        int side = roundedUp / 2;

        return side <= 1 ? (1, 2) : (side, side);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
