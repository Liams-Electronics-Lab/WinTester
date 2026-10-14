using System;
using System.Windows;

namespace WinTester.App;

// Exists to work around WPF airspace limitations against FurmarkEmbedHost's hosted native child window (see
// RunOverlayWindow.xaml's own comment for the full rationale). MainViewModel.EnterFullscreen/ExitFullscreen
// own this window's lifetime, creating/showing it right after the main window goes fullscreen and closing
// it when the run ends, alongside InputLockdown.
public partial class RunOverlayWindow : Window
{
    public RunOverlayWindow(object dataContext)
    {
        InitializeComponent();
        DataContext = dataContext;
        OverlayUiGrid.SizeChanged += (_, _) => UpdateStatsScale();
    }

    // Reference height at which the stats overlay is drawn at its designed size (scale 1.0). Taller windows scale
    // it up proportionally, capped so a very large display does not produce an absurd overlay. Both are tuning
    // constants. Values are WPF DIPs, so display scaling is already accounted for.
    private const double StatsReferenceHeight = 900;
    private const double StatsMinScale = 1.0, StatsMaxScale = 2.0;

    private void UpdateStatsScale()
    {
        var scale = Math.Clamp(OverlayUiGrid.ActualHeight / StatsReferenceHeight, StatsMinScale, StatsMaxScale);
        StatsScale.ScaleX = scale;
        StatsScale.ScaleY = scale;
    }

    // Sizes/positions this window to exactly cover the primary screen. Uses SystemParameters.PrimaryScreen-
    // Width/Height (WPF DIP units) rather than a device-pixel rect from Visual.PointToScreen, since mixing
    // DIP-based Window.Left/Top/Width/Height with device pixels is a common source of off-by-a-scale-factor
    // bugs on non-100%-DPI displays. Same single-primary-monitor kiosk assumption FurMark's own --width/
    // --height uses elsewhere in this app.
    public void CoverPrimaryScreen()
    {
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
    }
}
