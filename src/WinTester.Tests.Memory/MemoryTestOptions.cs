using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WinTester.Tests.Memory;

// Advanced Mode options for the memory test. Deliberately limited to Iterations and MaxTestMb; pattern set isn't selectable (the engine always runs all 8), and window/chunk sizing are internal tuning knobs, not operator-facing.
public sealed class MemoryTestOptions : INotifyPropertyChanged
{
    private int _iterations = MemoryStressTestModule.DefaultIterations;

    // Full passes through all 8 patterns; maps directly onto the engine's iterations parameter.
    public int Iterations
    {
        get => _iterations;
        set { if (_iterations == value) return; _iterations = value; OnPropertyChanged(); }
    }

    private int? _maxTestMb;

    // Null means no cap: use MemorySizer's full recommendation. When set, StartAsync clamps the recommendation down to this value only (never up, so it can't force more than the safety calculation allows) and notes the clamp in KeyMetrics.
    public int? MaxTestMb
    {
        get => _maxTestMb;
        set { if (_maxTestMb == value) return; _maxTestMb = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
