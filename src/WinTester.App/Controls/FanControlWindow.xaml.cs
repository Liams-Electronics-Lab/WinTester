using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using WinTester.App.ViewModels;
using WinTester.Core.Fans;
using WinTester.Tools.Fans;

namespace WinTester.App.Controls;

// "Fans" tool window. Lists every fan the selected backend can read an RPM for; a fan the backend also
// reports as writable gets an enabled 0-100% slider, everything else shows RPM only.
//
// Also renders a vendor tier row when the active backend implements IFanTierControl. Generic
// LibreHardwareMonitor and ASUS-desktop backends don't implement it, so nothing extra appears there;
// Gigabyte does, since its EC only genuinely honours discrete modes on some boards and a slider-only UI
// would look broken across most of its travel.
//
// Safety (non-negotiable): every manual duty this window sets must be handed back to automatic/factory
// control the moment the window closes, regardless of how it closes — Close button, titlebar X, Esc,
// Alt+F4, or app shutdown. Implemented as an OnClosed override (fires for all of those in WPF) plus an
// AppDomain.ProcessExit hook for the shutdown case. Neither covers a hard process kill — an inherent gap
// for any Windows app, not specific to this implementation.
public partial class FanControlWindow : Window
{
    private readonly IFanControlService _fanControlService;
    private readonly IFanTierControl? _tierControl;
    private readonly ObservableCollection<FanRowViewModel> _rows = new();
    private readonly ObservableCollection<FanTierViewModel> _tiers = new();
    private readonly DispatcherTimer _pollTimer;

    private FanControlWindow(Window owner, FanBackendSelectionResult selection)
    {
        InitializeComponent();
        Owner = owner;
        _fanControlService = selection.ActiveBackend;

        BackendText.Text = $"Backend: {selection.BackendDescription}";
        FanList.ItemsSource = _rows;
        TierList.ItemsSource = _tiers;

        // Capability check, not a vendor check — any backend gains a tier row purely by implementing
        // the interface.
        _tierControl = selection.ActiveBackend as IFanTierControl;
        InitializeTierPanel();

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _pollTimer.Tick += (_, _) => Poll();

        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

        Loaded += (_, _) =>
        {
            Poll();
            _pollTimer.Start();
        };
    }

    private void InitializeTierPanel()
    {
        if (_tierControl is null) return;

        IReadOnlyList<FanTier> tiers;
        try
        {
            tiers = _tierControl.GetTiers();
        }
        catch
        {
            // A backend that can't enumerate its own tiers is treated as having none.
            return;
        }

        if (tiers.Count == 0) return;

        foreach (var tier in tiers)
            _tiers.Add(new FanTierViewModel(tier));

        TierPanel.Visibility = Visibility.Visible;
    }

    private void Poll()
    {
        IReadOnlyList<FanControlInfo> fans;
        try
        {
            fans = _fanControlService.GetFans();
        }
        catch (Exception ex)
        {
            SummaryText.Text = $"Could not read fans: {ex.Message}";
            ShowPlaceholder(SummaryText.Text);
            return;
        }

        if (fans.Count == 0)
        {
            SummaryText.Text = "No fans reported on this machine.";
            ShowPlaceholder("The selected backend did not report any fan sensors on this machine.");
            return;
        }

        var controllableCount = 0;
        foreach (var fan in fans)
        {
            var row = FindOrCreateRow(fan);
            row.ApplyPoll(fan.Rpm, fan.DutyPercent);
            if (fan.IsControllable) controllableCount++;
        }

        SummaryText.Text = $"{fans.Count} fan{(fans.Count == 1 ? "" : "s")} detected, " +
                           $"{controllableCount} controllable.";

        RefreshActiveTier();
        HidePlaceholder();
    }

    // Read on every poll, not just after a button press, so the highlight also follows the hardware
    // into a tier reached by dragging a slider across a threshold.
    private void RefreshActiveTier()
    {
        if (_tierControl is null || _tiers.Count == 0) return;

        string? activeId;
        try
        {
            activeId = _tierControl.ActiveTierId;
        }
        catch
        {
            return;
        }

        foreach (var tier in _tiers)
            tier.IsActive = tier.Id == activeId;
    }

    private void TierButton_Click(object sender, RoutedEventArgs e)
    {
        if (_tierControl is null) return;
        if (sender is not Button { DataContext: FanTierViewModel tier }) return;

        try
        {
            _tierControl.ApplyTier(tier.Id);
        }
        catch (Exception ex)
        {
            SummaryText.Text = $"Could not apply mode: {ex.Message}";
            return;
        }

        // Reflect the new tier immediately rather than waiting for the next tick.
        Poll();
    }

    private FanRowViewModel FindOrCreateRow(FanControlInfo fan)
    {
        foreach (var existing in _rows)
        {
            if (existing.Id == fan.Id) return existing;
        }

        var row = new FanRowViewModel(fan.Id, fan.Name, fan.IsControllable);
        row.PropertyChanged += (sender, e) =>
        {
            if (e.PropertyName != nameof(FanRowViewModel.DutyPercent)) return;
            if (sender is not FanRowViewModel r || r.IsPollUpdateInProgress) return;

            // User dragged the slider: push it to the backend. Poll-driven updates are flagged via
            // IsPollUpdateInProgress so this doesn't chase the next poll's ApplyPoll call.
            _fanControlService.SetDuty(r.Id, r.DutyPercent);
        };

        _rows.Add(row);
        return row;
    }

    private void ShowPlaceholder(string message)
    {
        PlaceholderText.Text = message;
        PlaceholderText.Visibility = Visibility.Visible;
        FanList.Visibility = Visibility.Collapsed;
    }

    private void HidePlaceholder()
    {
        PlaceholderText.Visibility = Visibility.Collapsed;
        FanList.Visibility = Visibility.Visible;
    }

    private void ResetAllButton_Click(object sender, RoutedEventArgs e)
    {
        IssueResetAll();
        Poll(); // reflect the now-reset duty values immediately.
    }

    // Deliberately not latched to "only once": returning an already-automatic fan to automatic is a
    // harmless no-op on every backend here, so calling this from the manual button, OnClosed, and
    // ProcessExit costs nothing and keeps every path equally reliable.
    private void IssueResetAll()
    {
        try
        {
            _fanControlService.ResetAll();
        }
        catch
        {
            // Best-effort: a failed reset shouldn't block the window from closing.
        }
    }

    private void OnProcessExit(object? sender, EventArgs e) => IssueResetAll();

    protected override void OnClosed(EventArgs e)
    {
        _pollTimer.Stop();
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        IssueResetAll();

        // A backend holding a WMI connection and a worker thread (Gigabyte) needs tearing down too.
        // IssueResetAll runs first so the reset is never skipped by a Dispose that throws.
        if (_fanControlService is IDisposable disposable)
        {
            try { disposable.Dispose(); } catch { }
        }

        base.OnClosed(e);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    public static void Show(Window owner, FanBackendSelectionResult selection)
    {
        var window = new FanControlWindow(owner, selection);
        window.ShowDialog();
    }
}
