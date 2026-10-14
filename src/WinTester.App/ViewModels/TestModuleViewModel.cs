using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using WinTester.Core;

namespace WinTester.App.ViewModels;

public enum TestStatus
{
    Idle,
    Running,
    Pass,
    Fail
}

// Wraps a single ITestModule for tile display: selection state, run status, and the tile's color (resolved
// from TileColorKey). The tile grid is entirely data-driven off a collection of these, so adding a module
// means adding one entry to the composition root, not a new XAML tile.
public sealed class TestModuleViewModel : INotifyPropertyChanged
{
    // Default per-tile color palette, keyed by ITestModule.TileColorKey; distinct hues chosen to read
    // clearly against the #1C1C1F background while staying in the same dark, professional family as the
    // accent color. Per-tile brand colors would extend brand.json's schema and resolve through
    // IBrandingProvider instead of this static map.
    private static readonly Dictionary<string, string> DefaultTileColors = new()
    {
        ["TileCpu"] = "#2E4E8F",
        ["TileGpu"] = "#2E8B57",
        ["TileStorage"] = "#B8860B",
        ["TileMemory"] = "#7B4FA6",
        ["TilePower"] = "#1F9E9E",
    };

    public ITestModule Module { get; }

    public string Id => Module.Id;
    public string DisplayName => Module.DisplayName;
    public bool SupportsAdvancedSettings => Module.SupportsAdvancedSettings;

    public Brush TileBrush { get; }

    // S147: false when the module's assembly is missing (the Module is then an UnavailableTestModule): the tile is greyed
    // out, cannot be selected, run or configured. MissingFolder is the module folder name, e.g. "Tests.Cpu".
    public string? MissingFolder { get; }
    public bool IsAvailable => MissingFolder is null;
    public string? UnavailableToolTip => MissingFolder is null ? null : $"Module missing: {MissingFolder} ({ModuleScan.AssemblyFileFor(MissingFolder)} not found next to the exe)";

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    private TestStatus _status = TestStatus.Idle;
    public TestStatus Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusBrush));
        }
    }

    public Brush StatusBrush => Status switch
    {
        TestStatus.Running => BrandResourceAccessor.AccentBrush,
        TestStatus.Pass => new SolidColorBrush(Color.FromRgb(0x3C, 0xB0, 0x43)),
        TestStatus.Fail => new SolidColorBrush(Color.FromRgb(0xD9, 0x3B, 0x3B)),
        _ => Brushes.Transparent
    };

    public TestModuleViewModel(ITestModule module, string? missingFolder = null)
    {
        Module = module;
        MissingFolder = missingFolder;

        var hex = DefaultTileColors.TryGetValue(module.TileColorKey, out var found) ? found : "#33363E";
        TileBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        TileBrush.Freeze();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

// Reads the current accent brush (set at startup from brand.json via App.xaml.cs's ApplyBrandResources)
// through Application-wide DynamicResources.
internal static class BrandResourceAccessor
{
    public static Brush AccentBrush =>
        (Brush)(System.Windows.Application.Current.TryFindResource("AccentBrush") ?? Brushes.DodgerBlue);
}
