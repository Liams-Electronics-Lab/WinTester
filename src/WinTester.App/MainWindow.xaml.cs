using System;
using System.Windows;
using WinTester.App.ViewModels;
using WinTester.Branding;
using WinTester.Core;

namespace WinTester.App;

public partial class MainWindow : Window
{
    public MainWindow(IBrandingProvider brandingProvider, ISensorService sensorService)
    {
        InitializeComponent();

        Title = brandingProvider.Current.AppName;
        DataContext = new MainViewModel(brandingProvider.Current, this, sensorService);
    }

    protected override void OnClosed(EventArgs e)
    {
        (DataContext as MainViewModel)?.ShutdownHwInfo();
        base.OnClosed(e);
    }

    // Screen-pixel bounds of the running-overlay's fullscreen test-content layer (GpuFrameControl now fills
    // the entire overlay, so this is simply the whole window's bounds); MainViewModel uses it to tell the
    // GPU test where to position and size FurMark's window. Visual.PointToScreen already returns device
    // pixels [WPF handles the DPI conversion internally] so no manual DPI math is needed despite the
    // control's own coordinates being in DIPs.
    public Rect GetGpuFramePixelRect()
    {
        // Guarantees GpuFrameControl's ActualWidth/Height reflect this window's current WindowState even if
        // called before WPF's layout pass has caught up with a very recent resize/maximize, cheap
        // insurance against a stale, pre-maximize rect.
        UpdateLayout();

        var topLeft = GpuFrameControl.PointToScreen(new Point(0, 0));
        var bottomRight = GpuFrameControl.PointToScreen(new Point(GpuFrameControl.ActualWidth, GpuFrameControl.ActualHeight));
        return new Rect(topLeft, bottomRight);
    }

    // The native HWND of GpuFrameControl's FurmarkEmbedHost container, for GpuStressTestModule to reparent
    // FurMark's render window into. Returns IntPtr.Zero if the host hasn't built its native handle yet (e.g.
    // called before this window's first layout pass); FurmarkRunner already treats a zero parent handle as
    // "fall back to the positioned-top-level-window approach," so no separate null-handling contract is needed.
    public IntPtr GetGpuEmbedParentHandle()
    {
        UpdateLayout();
        return GpuFrameControl.GpuEmbedHost.ContainerHandle;
    }
}
