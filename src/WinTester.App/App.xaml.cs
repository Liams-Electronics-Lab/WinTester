using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using WinTester.Branding;
using WinTester.Core;
using WinTester.Sensors;
using WinTester.Sensors.HwInfo;

namespace WinTester.App;

public partial class App : Application
{
    // Splash must stay visible at least this long, centered, regardless of how fast brand/init work
    // actually finishes.
    private const int MinSplashDurationMs = 5000;

    private ISensorService? _sensorService;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Last-resort safety net: several tools in this codebase are known to throw exceptions that bubble up to the UI thread, which WPF will treat as fatal and terminate the process. This handler logs the exception and shows a message box, but allows the app to keep running.
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        _ = StartupSequenceAsync();
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            var logPath = Path.Combine(AppContext.BaseDirectory, "WinTester_crash.log");
            File.AppendAllText(logPath,
                $"{DateTime.Now:u}  Unhandled UI-thread exception (recovered):{Environment.NewLine}{e.Exception}{Environment.NewLine}{new string('-', 80)}{Environment.NewLine}");
        }
        catch
        {
            // Logging is best-effort; never let a failure here defeat the whole point of this handler.
        }

        MessageBox.Show(
            $"Something went wrong and has been logged (WinTester_crash.log):\n\n{e.Exception.Message}\n\nWinTester will keep running.",
            "WinTester — recovered from an error",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

        // Mark handled so WPF doesn't terminate the process — the whole point of this handler.
        e.Handled = true;
    }

    private async Task StartupSequenceAsync()
    {
        var brandingService = new BrandingService();
        brandingService.Load();
        var brand = brandingService.Current;

        ApplyBrandResources(brand);

        var splash = new SplashWindow(brand);
        splash.Show();

        var stopwatch = Stopwatch.StartNew();

        // Sensor-service initialization (device enumeration/driver access) runs here, in parallel with the
        // splash timer, so it's fully ready the instant the minimum splash duration elapses, same as the
        // main window.
        // Sensor Fallback, wrap the LHM backend
        // in the HWiNFO fallback decorator here, at the one place ISensorService is constructed, so every
        // consumer downstream (session sampler, Power/GPU/Storage modules, the Sensors tool) gets the
        // fallback for free without any of them changing how they call GetSnapshot().
        _sensorService = new HwInfoBackedSensorService(new LibreHardwareSensorService());
        await Task.Run(_sensorService.Initialize);

        var mainWindow = new MainWindow(brandingService, _sensorService);

        var remaining = MinSplashDurationMs - (int)stopwatch.ElapsedMilliseconds;
        if (remaining > 0)
            await Task.Delay(remaining);

        MainWindow = mainWindow;
        mainWindow.Show();
        splash.Close();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _sensorService?.Dispose();
        base.OnExit(e);
    }

    // Resolves brand.json's color strings into WPF brushes and publishes them as application-wide
    // DynamicResources. The only place brand hex values get read, every window/control references
    // {DynamicResource WindowBackgroundBrush}/{DynamicResource AccentBrush} etc., so a re-skin is a
    // brand.json edit, never a XAML or code change.
    private static void ApplyBrandResources(BrandProfile brand)
    {
        var res = Current.Resources;

        var windowBg = (Color)ColorConverter.ConvertFromString(brand.WindowBackgroundColor);
        var accent = (Color)ColorConverter.ConvertFromString(brand.AccentColor);
        var primary = (Color)ColorConverter.ConvertFromString(brand.PrimaryColor);

        res["WindowBackgroundBrush"] = Freeze(new SolidColorBrush(windowBg));
        res["AccentBrush"] = Freeze(new SolidColorBrush(accent));
        res["AccentBrushHover"] = Freeze(new SolidColorBrush(Lighten(accent, 0.15)));
        res["PrimaryBrush"] = Freeze(new SolidColorBrush(primary));
    }

    private static Color Lighten(Color c, double amount)
    {
        byte Adjust(byte channel) => (byte)Math.Min(255, channel + (255 - channel) * amount);
        return Color.FromRgb(Adjust(c.R), Adjust(c.G), Adjust(c.B));
    }

    private static SolidColorBrush Freeze(SolidColorBrush b)
    {
        b.Freeze();
        return b;
    }
}
