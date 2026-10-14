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
using WinTester.Tools.BatteryCharge;

namespace WinTester.App.Controls;

// Battery Charge Test tool window. Code-behind-only, owns its own runner, same posture as BatteryDrainWindow;
// adds a third panel (Waiting-for-charger) alongside Options/Running for the charger-connect gate this test needs.
public partial class BatteryChargeWindow : Window
{
    private readonly ISensorService _sensorService;
    private readonly BrandProfile? _brand;
    private BatteryChargeRunner? _runner;
    private CancellationTokenSource? _cts;
    private readonly List<BatteryChargeSample> _samples = new();
    private BatteryChargeOptions? _options;
    private BatteryChargeStartSnapshot? _startSnapshot;
    private bool _isRunning;
    private string? _reportPath;

    private readonly DispatcherTimer _uiTickTimer;
    private readonly DispatcherTimer _flashTimer;
    private DateTime _runStartTime;
    private bool _flashOn;

    private BatteryChargeWindow(Window owner, ISensorService sensorService, BrandProfile? brand)
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

    private void SweepMode_Checked(object sender, RoutedEventArgs e)
    {
        if (SweepModeDescriptionText is null) return; // fires once during InitializeComponent, before named fields exist

        var isPeakSweep = PeakSweepRadio.IsChecked == true;
        PeakStallOptionsPanel.Visibility = isPeakSweep ? Visibility.Visible : Visibility.Collapsed;

        SweepModeDescriptionText.Text = QuickTestRadio.IsChecked == true
            ? "Starts below 95% battery. Stops at 100%, zero current for 20 minutes, or manual stop. Chart starts at real elapsed time."
            : WideSweepRadio.IsChecked == true
                ? "Starts below 15% battery. Stops at 100%, zero current for 20 minutes, or manual stop. The 0-15% span is extrapolated on the chart."
                : "Starts below 50% battery. Stops at 100%, a stalled peak current, zero current for 20 minutes, or manual stop.";
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        OptionsErrorText.Visibility = Visibility.Collapsed;

        var mode = WideSweepRadio.IsChecked == true ? BatteryChargeSweepMode.WideSweep
            : QuickTestRadio.IsChecked == true ? BatteryChargeSweepMode.QuickTest
            : BatteryChargeSweepMode.PeakSweep;

        // Operator-configurable poll interval, defaulting to 5s via BatteryChargeOptions.PollInterval's own
        // property initializer. Validated the same way the gate check below is: fail fast in-window via
        // ShowOptionsError rather than letting a bad value reach the runner. Non-numeric or non-positive
        // input is rejected; there's no documented upper bound, so an extreme value (e.g. 3600) is trusted
        // rather than second-guessed.
        if (!double.TryParse(PollIntervalTextBox.Text, out var pollSeconds) || pollSeconds <= 0)
        {
            ShowOptionsError("Poll interval must be a positive number of seconds.");
            return;
        }

        // Peak-stall detection's gate/duration, same validate-in-window-fail-fast posture as PollInterval
        // above. Only meaningful for Peak Sweep, but always validated/passed through regardless of the
        // selected mode; BatteryChargeRunner already ignores it outside Peak Sweep.
        if (!double.TryParse(PeakStallMinPercentTextBox.Text, out var peakStallMinPercent) ||
            peakStallMinPercent < 0 || peakStallMinPercent > 100)
        {
            ShowOptionsError("Peak-stall Battery % must be between 0 and 100.");
            return;
        }
        if (!double.TryParse(PeakStallMinutesTextBox.Text, out var peakStallMinutes) || peakStallMinutes <= 0)
        {
            ShowOptionsError("Peak-stall minutes must be a positive number.");
            return;
        }

        var options = new BatteryChargeOptions
        {
            SweepMode = mode,
            UseGpuLoadInPrep = UseGpuLoadInPrepCheckBox.IsChecked == true,
            PollInterval = TimeSpan.FromSeconds(pollSeconds),
            EnablePeakStallDetection = EnablePeakStallDetectionCheckBox.IsChecked == true,
            PeakStallMinBatteryPercent = peakStallMinPercent,
            PeakStallStopAfter = TimeSpan.FromMinutes(peakStallMinutes)
        };

        // Fail-fast gate check against the live snapshot, ahead of the save dialog; the runner re-checks
        // authoritatively. Only rejects a battery already at 100%: above-threshold now triggers the runner's
        // own discharge-prep routine instead of failing here.
        var liveSnap = _sensorService.GetSnapshot();
        var gateError = options.ValidateStartGate(liveSnap.Battery?.Percent);
        if (gateError is not null)
        {
            ShowOptionsError(gateError);
            return;
        }

        var saveDialog = new SaveFileDialog
        {
            Title = "Save Battery Charge Test Report",
            FileName = $"Battery_Charge_Report_{DateTime.Now:yyyy-MM-dd_HHmmss}.html",
            Filter = "HTML report (*.html)|*.html",
            DefaultExt = ".html"
        };
        if (saveDialog.ShowDialog(this) != true) return;
        _reportPath = saveDialog.FileName;
        _options = options;

        await BeginRunAsync(options, _reportPath);
    }

    private void ShowOptionsError(string message)
    {
        OptionsErrorText.Text = message;
        OptionsErrorText.Visibility = Visibility.Visible;
    }

    // ===================== Run lifecycle =====================

    private async Task BeginRunAsync(BatteryChargeOptions options, string reportPath)
    {
        _samples.Clear();
        GraphCanvas.Children.Clear();
        RunningErrorText.Visibility = Visibility.Collapsed;

        OptionsPanel.Visibility = Visibility.Collapsed;
        WaitingPanel.Visibility = Visibility.Visible;
        WaitingTitleText.Text = "Please wait";
        WaitingStatusText.Text = "Checking battery state...";
        RunningPanel.Visibility = Visibility.Collapsed;

        _runner = new BatteryChargeRunner(_sensorService, brand: _brand);
        _runner.Started += snap => Dispatcher.Invoke(() => OnStarted(snap));
        _runner.SampleCaptured += s => Dispatcher.Invoke(() => OnSample(s));
        _runner.StatusChanged += msg => Dispatcher.Invoke(() => OnStatusChanged(msg));
        _runner.Completed += (end, path) => Dispatcher.Invoke(() => OnCompleted(end, path));
        _runner.Failed += msg => Dispatcher.Invoke(() => OnFailed(msg));

        _cts = new CancellationTokenSource();
        _isRunning = true;

        _ = Task.Run(() => _runner.RunAsync(options, reportPath, _cts.Token));
        await Task.CompletedTask;
    }

    private void OnStatusChanged(string message)
    {
        // Shared by the discharge-prep and connect-charger phases, both pre-Started; a keyword match on the
        // runner's own message picks the heading, and the message itself always fills the status line.
        if (WaitingPanel.Visibility != Visibility.Visible) return;

        WaitingTitleText.Text = message.Contains("unplug", StringComparison.OrdinalIgnoreCase) ||
                                 message.Contains("discharging", StringComparison.OrdinalIgnoreCase) ||
                                 message.Contains("stress test", StringComparison.OrdinalIgnoreCase)
            ? "Preparing battery"
            : "Connect the charger";
        WaitingStatusText.Text = message;
    }

    private void OnStarted(BatteryChargeStartSnapshot snap)
    {
        _startSnapshot = snap;
        _runStartTime = snap.StartTime;

        WaitingPanel.Visibility = Visibility.Collapsed;
        RunningPanel.Visibility = Visibility.Visible;
        TimerText.Foreground = new SolidColorBrush(Color.FromRgb(0x3C, 0xB0, 0x43));
        TimerText.FontSize = 34;
        TimerText.Text = "00:00:00:00";
        TimerText.Opacity = 1.0;
        StopButton.IsEnabled = true;
        CloseRunningButton.IsEnabled = false;
        _uiTickTimer.Start();

        StartTimeText.Text = snap.StartTime.ToString("HH:mm:ss");
        StartPercentText.Text = Fmt(snap.StartPercent, "0.0", "%");
        StartVoltageText.Text = Fmt(snap.StartVoltageV, "0.000", "V");
    }

    private void OnSample(BatteryChargeSample s)
    {
        _samples.Add(s);

        CurrentPercentText.Text = Fmt(s.BatteryPercent, "0.0", "%");
        CurrentVoltageText.Text = Fmt(s.VoltageV, "0.000", "V");
        ChargeCurrentText.Text = Fmt(s.ChargeCurrentMa, "0.0", "");
        ChargeRateText.Text = Fmt(s.ChargeRateW, "0.00", "");
        RemainingCapacityText.Text = Fmt(s.RemainingCapacityMwh, "0", "");
        TemperatureText.Text = Fmt(s.TemperatureC, "0.0", "");

        RedrawGraph();
    }

    private void OnCompleted(BatteryChargeEndSnapshot end, string reportPath)
    {
        _isRunning = false;
        _uiTickTimer.Stop();
        StopButton.IsEnabled = false;
        CloseRunningButton.IsEnabled = true;

        TimerText.Text = "TEST COMPLETE";
        TimerText.FontSize = 26;
        TimerText.Foreground = new SolidColorBrush(Color.FromRgb(0x3C, 0xB0, 0x43));
        _flashOn = true;
        _flashTimer.Start();

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
        _flashTimer.Stop();

        // A failure can land before the Waiting panel ever shows (e.g. desktop-classified machine) or while
        // still waiting on the charger; route the error to whichever panel is actually visible, not Running.
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
        // Doubles as the Waiting panel's Cancel button; RequestStop cancels the linked token whether waiting
        // or running. Cancelling during the charger-wait loop returns cleanly with no Completed/Failed, so
        // reset the UI to the options panel for that case specifically.
        StopButton.IsEnabled = false;
        CancelWaitButton.IsEnabled = false;
        _runner?.RequestStop();
        _cts?.Cancel();

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
        TimerText.Text = FormatTimer(elapsed); // counts up only; no fixed target; all three modes stop on %, stall, or manual.
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
        var xMax = Math.Max(lastElapsed * 1.05, 60);

        var polyline = new Polyline
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0x3C, 0xB0, 0x43)),
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
            "A Battery Charge Test is still running. Stop the test and close this window?");
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
        var window = new BatteryChargeWindow(owner, sensorService, brand);
        window.ShowDialog();
    }
}
