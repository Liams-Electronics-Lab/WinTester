using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WinTester.Tests.Gpu;

// OpenGL context for FurMark2's OpenGL baseline pass. Maps onto the same --opengl-ver-major/-minor/-core-profile flags GpuStressTestModule's compatibility-retry chain uses; lets an operator pick the first context tried instead of always starting from FurMark2's default and falling back only on failure.
public enum OpenGlVersionOption
{
    // No override; FurMark2 picks its own default context; the safe, pre-Advanced-Mode default.
    Auto,
    Core3_3,
    Compatibility3_3,
    Compatibility2_1
}

// Module-local Advanced Mode settings only; the global iGPU-if-dGPU-present and remote-desktop-check toggles live on WinTester.Core.GlobalTestOptions instead, shared with the Power tile rather than duplicated here. Mutable/read-fresh, same as CpuTestOptions.
public sealed class GpuTestOptions : INotifyPropertyChanged
{
    private OpenGlVersionOption _openGlVersion = OpenGlVersionOption.Auto;

    public OpenGlVersionOption OpenGlVersion
    {
        get => _openGlVersion;
        set { if (_openGlVersion == value) return; _openGlVersion = value; OnPropertyChanged(); }
    }

    // Resolves to the (major, minor, coreProfile) tuple FurmarkRunner.RunAsync's openGl* params expect, or null for Auto: FurmarkRunner treats null as "omit the flags entirely."
    public (int Major, int Minor, bool Core)? ResolveOpenGlContext() => OpenGlVersion switch
    {
        OpenGlVersionOption.Core3_3 => (3, 3, true),
        OpenGlVersionOption.Compatibility3_3 => (3, 3, false),
        OpenGlVersionOption.Compatibility2_1 => (2, 1, false),
        _ => null
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
