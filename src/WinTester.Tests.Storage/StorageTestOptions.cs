using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WinTester.Tests.Storage;

// Advanced-Mode options for the Storage test: pass count and per-drive skip-list. Mutable/read-fresh, same pattern as the other TestOptions classes.
public sealed class StorageTestOptions : INotifyPropertyChanged
{
    private int _passes = FioRunner.DefaultLoopCount;

    // Maps onto FioRunner.RunPassAsync's loopCount (fio's --loops); already a working ctor-level tunable, now surfaced for the operator. Default unchanged (FioRunner.DefaultLoopCount, currently 3).
    public int Passes
    {
        get => _passes;
        set { if (_passes == value) return; _passes = value; OnPropertyChanged(); }
    }

    // Keyed by DiskInfoProvider's PhysicalDriveIndex (Win32_DiskDrive.Index); stable within one Windows session. Options dialog and StorageStressTestModule.StartAsync each re-scan independently, close enough in time that the index won't drift under the operator.
    public HashSet<int> SkippedPhysicalDriveIndexes { get; } = new();

    // Phase 3.3: forces the full Seq1M Q8T1 + Rand4K Q1T1 suite on a disk StorageStressTestModule would
    // otherwise quick-pass (mechanical HDD, or a drive type WMI couldn't determine — see
    // DriveMediaKind.Unknown), per the operator's request to be able to override that default from Advanced
    // Mode. Same keying convention as SkippedPhysicalDriveIndexes; a disk present in both sets is skipped
    // entirely (StartAsync applies SkippedPhysicalDriveIndexes first), so "forced" here only has an effect
    // on a disk that's actually tested this run.
    public HashSet<int> ForceFullSuitePhysicalDriveIndexes { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
