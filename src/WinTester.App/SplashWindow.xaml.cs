using System;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinTester.Branding;

namespace WinTester.App;

// Borderless splash window, shown immediately on process start. App.xaml.cs guarantees it stays up at
// least 5 seconds, centered, regardless of how quickly brand loading/window construction finish, so it
// always reads as an intentional splash rather than a flash.
public partial class SplashWindow : Window
{
    public SplashWindow(BrandProfile brand)
    {
        InitializeComponent();

        AppNameText.Text = brand.AppName;
        VersionText.Text = $"v{AppInfo.Version}";
        StartModuleScan();

        // splash.bmp lives at the application root, not under /brand; resolved relative to
        // AppContext.BaseDirectory so it works from bin\Debug, bin\Release, or an installed location alike.
        var splashPath = Path.IsPathRooted(brand.SplashImagePath)
            ? brand.SplashImagePath
            : Path.Combine(AppContext.BaseDirectory, brand.SplashImagePath);

        if (File.Exists(splashPath))
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(splashPath, UriKind.Absolute);
                bmp.EndInit();

                SplashImage.Source = bmp;
                SplashImage.Visibility = Visibility.Visible;
                FallbackPanel.Visibility = Visibility.Collapsed;
            }
            catch
            {
                // Corrupt/unreadable image: fall back rather than crash startup.
            }
        }
    }

    // S147: walks ModuleScan.Folders one name at a time (a real file check per step), then leaves a one-line summary.
    // Finishes well inside the splash's 5 seconds; the timer stops when the splash closes.
    private DispatcherTimer? _scanTimer;
    private int _scanIndex;
    private readonly List<string> _missing = new();

    private void StartModuleScan()
    {
        var folders = ModuleScan.Folders;
        _scanTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        _scanTimer.Tick += (_, _) =>
        {
            if (_scanIndex < folders.Count)
            {
                var folder = folders[_scanIndex++];
                if (!ModuleScan.IsPresent(folder)) _missing.Add(folder);
                ScanText.Text = folder;
                return;
            }

            _scanTimer?.Stop();
            ScanText.Text = _missing.Count == 0
                ? $"{folders.Count} modules OK"
                : $"{_missing.Count} missing: {string.Join(", ", _missing)}";
        };
        Closed += (_, _) => _scanTimer?.Stop();
        _scanTimer.Start();
    }
}
