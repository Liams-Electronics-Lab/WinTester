using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WinTester.App.ViewModels;
using WinTester.Core.Devices;
using WinTester.Tools.Devices;

namespace WinTester.App.Controls;

// "Devices" tool window; plain code-behind, no MVVM binding, same shape as ActivationWindow/BitLockerWindow.
// Lists every device Windows reports a Configuration Manager problem for, in a Device Manager-style tree
// grouped by problem category and then by device class, with each device's error code and what it means.
// Read-only: double-click (or the Properties button) opens Windows' own device properties dialog, and the
// Open Device Manager button hands off entirely — nothing here changes a driver or a device's state.
public partial class DevicesWindow : Window
{
    // Amber for a driver problem, red for a device/hardware error, grey for deliberately disabled: the
    // same severity reading as the rest of the app's status colours, and it keeps the disabled group from
    // looking like a fault at a glance.
    private static readonly Brush DriverBrush = Frozen(0xE0, 0xA0, 0x30);
    private static readonly Brush DeviceErrorBrush = Frozen(0xD9, 0x3B, 0x3B);
    private static readonly Brush DisabledBrush = Frozen(0x7A, 0x7A, 0x82);
    private static readonly Brush CleanBrush = Frozen(0x3C, 0xB0, 0x43);

    private bool _isScanning;

    private DevicesWindow(Window owner)
    {
        InitializeComponent();
        Owner = owner;
        DeviceTree.SelectedItemChanged += (_, _) => UpdatePropertiesButtonState();
        Loaded += async (_, _) => await ScanAsync();
    }

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    private async Task ScanAsync()
    {
        if (_isScanning) return;
        _isScanning = true;

        RefreshButton.IsEnabled = false;
        PropertiesButton.IsEnabled = false;
        ActionErrorText.Visibility = Visibility.Collapsed;
        DeviceTree.ItemsSource = null;

        SummaryText.Text = "Scanning devices...";
        ShowPlaceholder("Scanning devices...\nThis can take a few seconds on a machine with a lot of hardware.");

        try
        {
            var snapshot = await ProblemDeviceScanner.ScanAsync();
            Apply(snapshot);
        }
        finally
        {
            _isScanning = false;
            RefreshButton.IsEnabled = true;
            UpdatePropertiesButtonState();
        }
    }

    private void Apply(DeviceProblemSnapshot snapshot)
    {
        if (snapshot.ErrorMessage is { Length: > 0 } error)
        {
            SummaryText.Text = "Device status could not be read.";
            SummaryText.Foreground = DriverBrush;
            ShowPlaceholder(error);
            return;
        }

        if (!snapshot.HasProblems)
        {
            SummaryText.Text = $"No device problems found ({snapshot.TotalDevicesScanned} devices checked).";
            SummaryText.Foreground = CleanBrush;
            ShowPlaceholder("Every device Windows knows about reports that it is working properly.");
            return;
        }

        var deviceCount = snapshot.Devices.Count;
        SummaryText.Text =
            $"{deviceCount} device{(deviceCount == 1 ? "" : "s")} need attention " +
            $"({snapshot.TotalDevicesScanned} devices checked).";
        SummaryText.Foreground = DriverBrush;

        DeviceTree.ItemsSource = BuildTree(snapshot.Devices);
        PlaceholderText.Visibility = Visibility.Collapsed;
        DeviceTree.Visibility = Visibility.Visible;
    }

    // Category → device class → device, mirroring how Device Manager nests its own tree one level deeper
    // than a flat list. Ordering is entirely the scanner's (it sorts before returning), so this only
    // groups; it never re-sorts, which keeps "what order do rows appear in" answerable in one place.
    private static List<DeviceTreeNodeViewModel> BuildTree(IReadOnlyList<ProblemDevice> devices)
    {
        return devices
            .GroupBy(d => d.Category)
            .Select(categoryGroup => new DeviceTreeNodeViewModel
            {
                Label = DeviceErrorCodes.CategoryDisplayName(categoryGroup.Key),
                CountText = CountLabel(categoryGroup.Count()),
                AccentBrush = BrushFor(categoryGroup.Key),
                Children = categoryGroup
                    .GroupBy(d => d.ClassDisplayName)
                    .Select(classGroup => new DeviceTreeNodeViewModel
                    {
                        Label = classGroup.Key,
                        CountText = CountLabel(classGroup.Count()),
                        AccentBrush = BrushFor(categoryGroup.Key),
                        Children = classGroup.Select(ToDeviceNode).ToList()
                    })
                    .ToList()
            })
            .ToList();
    }

    private static DeviceTreeNodeViewModel ToDeviceNode(ProblemDevice device) => new()
    {
        Label = device.Name,
        ErrorText = $"Code {device.ErrorCode} — {device.ErrorSummary}",
        DetailText = device.Manufacturer is { Length: > 0 } manufacturer
            ? $"{device.ErrorDetail}  ({manufacturer})"
            : device.ErrorDetail,
        AccentBrush = BrushFor(device.Category),
        Device = device
    };

    private static string CountLabel(int count) => count == 1 ? "1 device" : $"{count} devices";

    private static Brush BrushFor(DeviceProblemCategory category) => category switch
    {
        DeviceProblemCategory.DriverProblem => DriverBrush,
        DeviceProblemCategory.DeviceError => DeviceErrorBrush,
        DeviceProblemCategory.Disabled => DisabledBrush,
        _ => DisabledBrush
    };

    private void ShowPlaceholder(string message)
    {
        PlaceholderText.Text = message;
        PlaceholderText.Visibility = Visibility.Visible;
        DeviceTree.Visibility = Visibility.Collapsed;
    }

    // Only a device row can have properties opened; group rows are selectable but do nothing.
    private void UpdatePropertiesButtonState() =>
        PropertiesButton.IsEnabled = !_isScanning && SelectedDevice() is not null;

    private ProblemDevice? SelectedDevice() =>
        (DeviceTree.SelectedItem as DeviceTreeNodeViewModel)?.Device;

    private void DeviceTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // A double-click on the expander toggle also bubbles up here, but the toggle sits on a group row,
        // whose node has no Device, so the guard inside OpenProperties covers that case without needing to
        // inspect the hit-test source.
        if (SelectedDevice() is null) return;

        e.Handled = true;
        OpenProperties();
    }

    private void PropertiesButton_Click(object sender, RoutedEventArgs e) => OpenProperties();

    private void OpenProperties()
    {
        ActionErrorText.Visibility = Visibility.Collapsed;

        if (SelectedDevice() is not { } device) return;

        var result = DeviceManagerLauncher.OpenDeviceProperties(device.DeviceId);
        if (!result.Success)
            ShowActionError(result.ErrorMessage ?? "Could not open device properties.");
    }

    private void OpenDeviceManagerButton_Click(object sender, RoutedEventArgs e)
    {
        ActionErrorText.Visibility = Visibility.Collapsed;

        var result = DeviceManagerLauncher.OpenDeviceManager();
        if (!result.Success)
            ShowActionError(result.ErrorMessage ?? "Could not open Device Manager.");
    }

    private void ShowActionError(string message)
    {
        ActionErrorText.Text = message;
        ActionErrorText.Visibility = Visibility.Visible;
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

    public static void Show(Window owner)
    {
        var window = new DevicesWindow(owner);
        window.ShowDialog();
    }
}
