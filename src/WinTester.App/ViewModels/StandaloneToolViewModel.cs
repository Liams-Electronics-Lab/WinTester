using System;
using System.Windows.Input;

namespace WinTester.App.ViewModels;

// Backs one Tab 2 ("Interactive/Long-Running") tile. Deliberately thinner than TestModuleViewModel/
// ITestModule: these are on-demand utilities (open a window, do its own thing, close) rather than auto-run
// queue members with Pass/Fail/progress/telemetry. Wraps a display name and an "open" action directly
// rather than an IStandaloneTool instance, since Windows Activation is a modal dialog with no running/
// stoppable lifetime; a genuinely long-running tool can still implement IStandaloneTool separately.
public sealed class StandaloneToolViewModel
{
    public string DisplayName { get; }

    // Resource key into Resources/Icons.xaml (e.g. "keyIcon", "lockIcon"); defaults to "keyIcon" so the
    // original Windows Activation registration doesn't need updating. Only used as a fallback when
    // IconFileName below isn't set: StandaloneToolTileControl prefers the raster icon whenever one exists.
    public string IconKey { get; }

    // Raster PNG icon file name under vendor/icons/ next to the exe (e.g. "drain.png"), same loose-file,
    // rebuild-free swap posture as Tab 1's ModuleIdToIconConverter icons. Null falls back to the vector
    // IconKey geometry above, so a future tile can still ship without commissioning custom art first.
    public string? IconFileName { get; }

    public ICommand OpenCommand { get; }

    // S147: the module folder this tile depends on (e.g. "Tools.Activation"); null for a tile that has no module of its own.
    // A tile whose module is missing (ModuleScan) is greyed out and does nothing when clicked.
    public string? ModuleFolder { get; }
    public bool IsAvailable { get; }
    public string? UnavailableToolTip { get; }

    public StandaloneToolViewModel(string displayName, Action open, string iconKey = "keyIcon", string? iconFileName = null,
        string? moduleFolder = null)
    {
        DisplayName = displayName;
        IconKey = iconKey;
        IconFileName = iconFileName;
        ModuleFolder = moduleFolder;
        IsAvailable = moduleFolder is null || ModuleScan.IsPresent(moduleFolder);
        UnavailableToolTip = IsAvailable ? null : $"Module missing: {moduleFolder} ({ModuleScan.AssemblyFileFor(moduleFolder!)} not found next to the exe)";
        OpenCommand = new RelayCommand(open, () => IsAvailable);
    }
}
