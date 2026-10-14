using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WinTester.Core;

// Settings shared between the GPU and Power tiles (marked "(global)" in each tile's Advanced Mode
// dialog). One instance is owned by MainViewModel and shared by reference with both modules, so editing
// either tile's dialog keeps them in sync. A plain mutable class rather than a record: both consumers
// read current values at StartAsync time, not just at construction.
public sealed class GlobalTestOptions : INotifyPropertyChanged
{
    private bool _runGpuTestOnIntegratedIfDedicatedPresent = true;

    // Default true: exercises every detected GPU. Set false to skip the integrated GPU's sweep whenever
    // a dedicated GPU is also present; has no effect on a single-GPU machine.
    public bool RunGpuTestOnIntegratedIfDedicatedPresent
    {
        get => _runGpuTestOnIntegratedIfDedicatedPresent;
        set { if (_runGpuTestOnIntegratedIfDedicatedPresent == value) return; _runGpuTestOnIntegratedIfDedicatedPresent = value; OnPropertyChanged(); }
    }

    private bool _enableRemoteDesktopCheck = true;

    // Default true: skips FurMark2/GravityMark under a Remote Desktop session to avoid a known
    // display-driver hang. Set false only when the operator is confident the RDP setup is safe.
    public bool EnableRemoteDesktopCheck
    {
        get => _enableRemoteDesktopCheck;
        set { if (_enableRemoteDesktopCheck == value) return; _enableRemoteDesktopCheck = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
