using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WinTester.Tests.Cpu;

// Which concurrent IPDT module pair to run; mirrors CpuStressTestModule's existing vendor-based auto-selection, exposed as an explicit Advanced Mode override for an operator who wants to force one pair regardless of detected vendor (e.g. comparing the two on the same machine, or working around a module that's misbehaving on a specific CPU).
public enum CpuModuleSet
{
    // Vendor-detected default (dgemm+fma3 on Intel, math_primenumber+fma3 elsewhere), unchanged from the pre-Advanced-Mode behavior. The safe, recommended default.
    Auto,
    DgemmFma3,
    MathPrimeNumberFma3
}

// Advanced Mode options for the CPU test. Mutable so the CPU tile's options dialog can edit it live on CpuStressTestModule.Options between runs without reconstructing the module, same pattern as WinTester.Core.GlobalTestOptions.
public sealed class CpuTestOptions : INotifyPropertyChanged
{
    private int _durationSeconds = CpuStressTestModule.DefaultDurationSeconds;

    // Seconds the stress load runs for. No hardcoded max: a shop may legitimately want a multi-hour burn-in as easily as a 30s smoke test. The dialog clamps to a sane minimum (see TestOptionsWindow) so a fat-fingered "0" doesn't produce a test that reports done instantly.
    public int DurationSeconds
    {
        get => _durationSeconds;
        set { if (_durationSeconds == value) return; _durationSeconds = value; OnPropertyChanged(); }
    }

    private CpuModuleSet _moduleSet = CpuModuleSet.Auto;

    public CpuModuleSet ModuleSet
    {
        get => _moduleSet;
        set { if (_moduleSet == value) return; _moduleSet = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
