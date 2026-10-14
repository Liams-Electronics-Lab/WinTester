using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using WinTester.Branding;
using WinTester.Core;
using WinTester.Tools.BatteryDrain;

namespace WinTester.App.Controls;

// Battery Drain tool window. Code-behind-only like BitLockerWindow/ActivationWindow, but a genuinely
// long-running background op with a live view, so it owns a BatteryDrainRunner and marshals its events to the
// UI thread itself rather than staying a single request/response round-trip. Two panels (Options/Running)
// toggled by visibility instead of separate windows, keeping one owner/modal relationship throughout.
public partial class BatteryDrainWindow : Window
{
    private readonly ISensorService _sensorService;
    private readonly BrandProfile? _brand;
    private BatteryDrainRunner? _runner;
    private CancellationTokenSource? _cts;
    private readonly List<BatteryDrainSample> _samples = new();
    private BatteryDrainOptions? _options;
    private BatteryDrainStartSnapshot? _startSnapshot;
    private bool _isRunning;
    private string? _reportPath;

    private readonly DispatcherTimer _uiTickTimer; // drives the countdown every 100ms, independent of the runner's 1s sample cadence, so it reads smoothly instead of jumping once a second.
    private readonly DispatcherTimer _flashTimer;  // "CYCLE COMPLETE" flash, started only on completion.
    private DateTime _runStartTime;
    private bool _flashOn;

    private BatteryDrainWindow(Window owner, ISensorService sensorService, BrandProfile? brand)
    {
        InitializeComponent();
        Owner = owner;
        _sensorService = sensorService;
        _brand = brand;

        _uiTickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _uiTickTimer.Tick += (_, _) => UpdateTimerText();

        _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _flashTimer.Tick += (_, _) =>
        {
            _flashOn = !_flashOn;
            TimerText.Opacity = _flashOn ? 1.0 : 0.35;
        };
    }

    // ===================== Options panel =====================

    private void LengthPreset_Checked(object sender, RoutedEventArgs e)
    {
        if (CustomLengthPanel is null) return; // fires once during InitializeComponent, before named fields exist
        CustomLengthPanel.IsEnabled = LengthCustomRadio.IsChecked == true;
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        OptionsErrorText.Visibility = Visibility.Collapsed;

        if (!int.TryParse(StopPercentTextBox.Text, out var stopPercent) || stopPercent < 1 || stopPercent > 95)
        {
            ShowOptionsError("Stop percentage must be a whole number between 1 and 95.");
            return;
        }

        TimeSpan? maxDuration = null;
        if (Length3hRadio.IsChecked == true) maxDuration = TimeSpan.FromHours(3);
        else if (Length2hRadio.IsChecked == true) maxDuration = TimeSpan.FromHours(2);
        else if (Length1hRadio.IsChecked == true) maxDuration = TimeSpan.FromHours(1);
        else if (LengthCustomRadio.IsChecked == true)
        {
            if (!int.TryParse(CustomHoursTextBox.Text, out var hrs) || hrs < 0 ||
                !int.TryParse(CustomMinutesTextBox.Text, out var mins) || mins < 0 || mins > 59 ||
                (hrs == 0 && mins == 0))
            {
                ShowOptionsError("Enter a valid custom length (hours ≥ 0, minutes 0-59, not both zero).");
                return;
            }
            maxDuration = new TimeSpan(hrs, mins, 0);
        }
        // else: Infinite radio checked, maxDuration stays null.

        if (CpuLoadCheckBox.IsChecked != true && GpuLoadCheckBox.IsChecked != true)
        {
            ShowOptionsError("Select at least one of CPU stress / GPU stress to drive the drain.");
            return;
        }

        var method = (CpuLoadCheckBox.IsChecked == true, GpuLoadCheckBox.IsChecked == true) switch
        {
            (true, true) => BatteryDrainMethod.CpuAndGpu,
            (true, false) => BatteryDrainMethod.CpuOnly,
            (false, true) => BatteryDrainMethod.GpuOnly,
            _ => BatteryDrainMethod.CpuOnly // unreachable, guarded above
        };

        var saveDialog = new SaveFileDialog
        {
            Title = "Save Battery Drain Report",
            FileName = $"Battery_Drain_Report_{DateTime.Now:yyyy-MM-dd_HHmmss}.html",
            Filter = "HTML report (*.html)|*.html",
            DefaultExt = ".html"
        };
        if (saveDialog.ShowDialog(this) != true) return;
        _reportPath = saveDialog.FileName;

        _options = new BatteryDrainOptions { StopPercent = stopPercent, MaxDuration = maxDuration, Method = method };

        BeginRun(_options, _reportPath);
    }

    private void ShowOptionsError(string message)
    {
        OptionsErrorText.Text = message;
        OptionsErrorText.Visibility = Visibility.Visible;
    }

    // ===================== Run lifecycle =====================

    private void BeginRun(BatteryDrainOptions options, string reportPath)
    {
        _samples.Clear();
        GraphCanvas.Children.Clear();
        RunningErrorText.Visibility = Visibility.Collapsed;
        TimerText.Foreground = new SolidColorBrush(Color.FromRgb(0xD9, 0x3B, 0x3B));
        TimerText.FontSize = 34;
        TimerText.Text = "00:00:00:00";
        TimerText.Opacity = 1.0;
        StopParamsText.Text = options.ToString();

        OptionsPanel.Visibility = Visibility.Collapsed;
        WaitingPanel.Visibility = Visibility.Visible;
        WaitingTitleText.Text = "Please wait";
        WaitingStatusText.Text = "Checking power state...";
        RunningPanel.Visibility = Visibility.Collapsed;

        _runner = new BatteryDrainRunner(_sensorService, brand: _brand);
        _runner.Started += snap => Dispatcher.Invoke(() => OnStarted(snap));
        _runner.SampleCaptured += s => Dispatcher.Invoke(() => OnSample(s));
        _runner.StatusChanged += msg => Dispatcher.Invoke(() => OnStatusChanged(msg));
        _runner.Completed += (end, path) => Dispatcher.Invoke(() => OnCompleted(end, path));
        _runner.Failed += msg => Dispatcher.Invoke(() => OnFailed(msg));

        _cts = new CancellationTokenSource();
        _isRunning = true;

        // Timer/uiTickTimer intentionally not started here, see OnStarted. The unplug-charger gate can
        // sit waiting for an arbitrary amount of time before the run actually begins, and that wait
        // shouldn't count against the run's own elapsed display.
        _ = Task.Run(() => _runner.RunAsync(options, reportPath, _cts.Token));
    }

    // Shared by the pre-run "unplug charger" gate; the runner's own message always fills the status line.
    private void OnStatusChanged(string message)
    {
        if (WaitingPanel.Visibility != Visibility.Visible) return;

        WaitingTitleText.Text = message.Contains("unplug", StringComparison.OrdinalIgnoreCase)
            ? "Unplug the charger"
            : "Please wait";
        WaitingStatusText.Text = message;
    }

    private void OnStarted(BatteryDrainStartSnapshot snap)
    {
        _startSnapshot = snap;
        _runStartTime = snap.StartTime;

        WaitingPanel.Visibility = Visibility.Collapsed;
        RunningPanel.Visibility = Visibility.Visible;
        StopButton.IsEnabled = true;
        CloseRunningButton.IsEnabled = false;
        _uiTickTimer.Start();

        StartTimeText.Text = snap.StartTime.ToString("HH:mm:ss");
        StartPercentText.Text = Fmt(snap.StartPercent, "0.0", "%");
        StartVoltageText.Text = Fmt(snap.StartVoltageV, "0.00", "V");
        DesignCapacityText.Text = Fmt(snap.DesignCapacityMwh, "0", "");
        FullChargeCapacityText.Text = Fmt(snap.FullChargeCapacityMwh, "0", "");
        StartHealthText.Text = Fmt(snap.StartingBatteryHealthPercent, "0.0", "%");
        RemainingCapacityText.Text = Fmt(snap.RemainingCapacityMwh, "0", "");

        CycleCountText.Text = snap.CycleCount is { } cc ? cc.ToString() : "N/A";
        ChemistryText.Text = FmtStr(snap.Chemistry);
        SerialNumberText.Text = FmtStr(snap.SerialNumber);
        ManufacturerDeviceText.Text = snap.ManufacturerName is null && snap.DeviceName is null
            ? "N/A"
            : $"{snap.ManufacturerName ?? "N/A"} / {snap.DeviceName ?? "N/A"}";
    }

    // String counterpart to Fmt: same explicit-N/A-never-blank convention, for the string-valued IOCTL-only battery fields.
    private static string FmtStr(string? v) => string.IsNullOrWhiteSpace(v) ? "N/A" : v;

    private void OnSample(BatteryDrainSample s)
    {
        _samples.Add(s);

        CurrentPercentText.Text = Fmt(s.BatteryPercent, "0.0", "%");
        CurrentVoltageText.Text = Fmt(s.VoltageV, "0.00", "V");
        RemainingCapacityText.Text = Fmt(s.RemainingCapacityMwh, "0", "");
        EstFullCapacityText.Text = Fmt(s.EstimatedFullChargeCapacityMwh, "0", "");
        CpuLoadTempText.Text = $"{Fmt(s.CpuLoadPercent, "0", "%")} | {Fmt(s.CpuTempC, "0", "C")}";
        GpuLoadTempText.Text = $"{Fmt(s.GpuLoadPercent, "0", "%")} | Core {Fmt(s.GpuTempC, "0", "C")} / Hot {Fmt(s.GpuHotspotTempC, "0", "C")}";

        RedrawGraph();
    }

    private void OnCompleted(BatteryDrainEndSnapshot end, string reportPath)
    {
        _isRunning = false;
        _uiTickTimer.Stop();
        StopButton.IsEnabled = false;
        CloseRunningButton.IsEnabled = true;

        TimerText.Text = "CYCLE COMPLETE";
        TimerText.FontSize = 28;
        TimerText.Foreground = new SolidColorBrush(Color.FromRgb(0x3C, 0xB0, 0x43));
        _flashOn = true;
        _flashTimer.Start();

        // Refresh the IOCTL-only fields from the end-of-run read; cycle count could tick up over a long
        // enough run, and CaptureEndSnapshot already read it, so this costs nothing.
        if (end.CycleCount is { } cc) CycleCountText.Text = cc.ToString();
        if (end.Chemistry is not null) ChemistryText.Text = end.Chemistry;
        if (end.SerialNumber is not null) SerialNumberText.Text = end.SerialNumber;
        if (end.ManufacturerName is not null || end.DeviceName is not null)
            ManufacturerDeviceText.Text = $"{end.ManufacturerName ?? "N/A"} / {end.DeviceName ?? "N/A"}";

        try
        {
            Process.Start(new ProcessStartInfo(reportPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            RunningErrorText.Text = $"Report saved to {reportPath}, but couldn't auto-open it: {ex.Message}";
            RunningErrorText.Visibility = Visibility.Visible;
        }
    }

    private void OnFailed(string message)
    {
        _isRunning = false;
        _uiTickTimer.Stop();

        // A failure can land while still waiting on the unplug gate; route it back to the options panel
        // instead of a Running panel that never actually got shown.
        if (WaitingPanel.Visibility == Visibility.Visible)
        {
            WaitingPanel.Visibility = Visibility.Collapsed;
            OptionsPanel.Visibility = Visibility.Visible;
            ShowOptionsError(message);
            return;
        }

        StopButton.IsEnabled = false;
        CloseRunningButton.IsEnabled = true;
        RunningErrorText.Text = message;
        RunningErrorText.Visibility = Visibility.Visible;
    }

    // ===================== Title bar (drag) =====================

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        // Doubles as the Waiting panel's Cancel button; RequestStop cancels the linked token whether
        // waiting or running. Cancelling during the unplug-wait gate returns cleanly with no
        // Completed/Failed, so reset the UI to the options panel for that case specifically.
        StopButton.IsEnabled = false;
        CancelWaitButton.IsEnabled = false;
        _runner?.RequestStop();

        if (WaitingPanel.Visibility == Visibility.Visible)
        {
            WaitingPanel.Visibility = Visibility.Collapsed;
            OptionsPanel.Visibility = Visibility.Visible;
            _isRunning = false;
            CancelWaitButton.IsEnabled = true;
        }
    }

    // ===================== Timer / graph =====================

    private void UpdateTimerText()
    {
        if (!_isRunning) return;

        var elapsed = DateTime.Now - _runStartTime;
        if (_options?.MaxDuration is { } maxD)
        {
            var remaining = maxD - elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                // Configured length elapsed, but the runner hasn't fired Completed yet; it still has to
                // detect the stop on its next 1s tick, cancel the CPU/GPU engines, wait for them to exit, and
                // write the report. Freezing at 00:00:00:00 read as a finished run with a stale "in progress"
                // report, so show an explicit wrapping-up state instead.
                TimerText.FontSize = 22;
                TimerText.Text = "Finishing up…";
                return;
            }
            TimerText.Text = FormatTimer(remaining);
        }
        else
        {
            TimerText.Text = FormatTimer(elapsed); // infinite mode counts up: no target to count down to.
        }
    }

    private static string FormatTimer(TimeSpan t) =>
        $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}:{t.Milliseconds / 10:00}";

    private void GraphCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RedrawGraph();

    private void RedrawGraph()
    {
        GraphCanvas.Children.Clear();
        var width = GraphCanvas.ActualWidth;
        var height = GraphCanvas.ActualHeight;
        if (width <= 0 || height <= 0 || _samples.Count == 0) return;

        // Gridlines at 0/25/50/75/100% with labels.
        for (var pct = 0; pct <= 100; pct += 25)
        {
            var y = height - pct / 100.0 * height;
            var line = new Line { X1 = 0, Y1 = y, X2 = width, Y2 = y, Stroke = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), StrokeThickness = 1 };
            GraphCanvas.Children.Add(line);

            var label = new TextBlock { Text = $"{pct}%", Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x93)), FontSize = 10 };
            Canvas.SetLeft(label, 4);
            Canvas.SetTop(label, Math.Max(0, y - 12));
            GraphCanvas.Children.Add(label);
        }

        var lastElapsed = _samples[^1].ElapsedSeconds;
        var xMax = _options?.MaxDuration is { } maxD ? maxD.TotalSeconds : Math.Max(lastElapsed * 1.05, 60);

        var polyline = new Polyline
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0xD9, 0x3B, 0x3B)),
            StrokeThickness = 2
        };

        foreach (var s in _samples)
        {
            if (s.BatteryPercent is not { } pct) continue;
            var x = xMax > 0 ? s.ElapsedSeconds / xMax * width : 0;
            var y = height - Math.Clamp(pct, 0, 100) / 100.0 * height;
            polyline.Points.Add(new Point(x, y));
        }

        GraphCanvas.Children.Add(polyline);
    }

    private static string Fmt(double? v, string format, string suffix) =>
        v is { } d ? d.ToString(format, CultureInfo.InvariantCulture) + suffix : "N/A";

    // ===================== Close / keyboard =====================

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (!_isRunning) Close();
        }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_isRunning) return;

        var confirmed = ConfirmDialog.Show(this,
            "A Battery Drain test is still running. Stop the test and close this window?");
        if (!confirmed)
        {
            e.Cancel = true;
            return;
        }

        _runner?.RequestStop();
        _cts?.Cancel();
    }

    public static void Show(Window owner, ISensorService sensorService, BrandProfile? brand = null)
    {
        var window = new BatteryDrainWindow(owner, sensorService, brand);
        window.ShowDialog();
    }
}
