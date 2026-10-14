using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WinTester.App.ViewModels;
using WinTester.Tools.DiskInfo;

namespace WinTester.App.Controls;

// "Disk Info" tool window; plain code-behind like DevicesWindow. Shows SMART health, key drive counters and the
// partition layout for one physical disk chosen in the drop-down, which starts on the disk Windows boots from.
// Read-only: nothing here writes to a disk, changes a partition or sends a vendor command, and the Open Disk Management
// button hands every change off to Windows.
public partial class DiskInfoWindow : Window
{
    private static readonly Brush AmberBrush = Frozen(0xE0, 0xA0, 0x30);
    private static readonly Brush MutedBrush = Frozen(0xB7, 0xB7, 0xBE);
    private static readonly Brush BlockBorder = Frozen(0x3D, 0x3D, 0x42);
    private static readonly Brush BlockBackground = Frozen(0x1E, 0x1E, 0x22);
    private static readonly Brush AllocatedStrip = Frozen(0x42, 0x6B, 0xFF);
    private static readonly Brush UnallocatedStrip = Frozen(0x0B, 0x0B, 0x0D);

    // Health box colours, light to dark, in the CrystalDiskInfo manner: Good blue, Caution amber, Bad red, Unknown grey.
    private static readonly (Color Top, Color Bottom) GoodColours = (Color.FromRgb(0x5B, 0x8C, 0xFF), Color.FromRgb(0x2F, 0x4F, 0xB5));
    private static readonly (Color Top, Color Bottom) CautionColours = (Color.FromRgb(0xE8, 0xB4, 0x4A), Color.FromRgb(0xA8, 0x74, 0x14));
    private static readonly (Color Top, Color Bottom) BadColours = (Color.FromRgb(0xE8, 0x5A, 0x5A), Color.FromRgb(0x9E, 0x24, 0x24));
    private static readonly (Color Top, Color Bottom) UnknownColours = (Color.FromRgb(0x7C, 0x7C, 0x86), Color.FromRgb(0x4A, 0x4A, 0x52));

    // Smallest share of the strip a block may take, so a 100 MB partition on a large disk stays readable.
    private const double MinimumBlockShare = 0.09;

    private IReadOnlyList<PhysicalDiskItem> _disks = Array.Empty<PhysicalDiskItem>();
    private PhysicalDiskItem? _current;
    private int _generation;
    private bool _loadingList;

    private DiskInfoWindow(Window owner)
    {
        InitializeComponent();
        Owner = owner;
        Loaded += async (_, _) => await LoadDisksAsync();
    }

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    // ---- loading -----------------------------------------------------------------------------------------------

    private async System.Threading.Tasks.Task LoadDisksAsync()
    {
        _loadingList = true;
        RefreshButton.IsEnabled = false;
        ShowReading("Looking for disks...");

        var list = await DiskInfoService.ListDisksAsync();
        _disks = list.Disks;

        DiskBox.ItemsSource = _disks.Select(d => d.DisplayName).ToList();
        _loadingList = false;
        RefreshButton.IsEnabled = true;

        if (_disks.Count == 0)
        {
            ShowFailure(list.ErrorMessage ?? "No disks were found.");
            return;
        }

        // The Windows disk first; if Windows could not say which one that is, the first disk.
        var start = _disks.ToList().FindIndex(d => d.IsWindowsDisk);
        DiskBox.SelectedIndex = start >= 0 ? start : 0;
    }

    private async void DiskBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingList) return;
        await LoadReportAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (_disks.Count == 0 || DiskBox.SelectedIndex < 0)
            await LoadDisksAsync();
        else
            await LoadReportAsync();
    }

    private async System.Threading.Tasks.Task LoadReportAsync()
    {
        var index = DiskBox.SelectedIndex;
        if (index < 0 || index >= _disks.Count) return;

        var disk = _disks[index];
        _current = disk;
        var generation = ++_generation;

        ShowReading($"Reading {disk.DisplayName}...");
        RefreshButton.IsEnabled = false;
        try
        {
            var report = await DiskInfoService.BuildReportAsync(disk);

            // A slower earlier read must not overwrite the disk chosen since.
            if (generation != _generation) return;
            Apply(report);
        }
        finally
        {
            if (generation == _generation) RefreshButton.IsEnabled = true;
        }
    }

    // ---- display -----------------------------------------------------------------------------------------------

    private void Apply(DiskReport report)
    {
        var disk = report.Disk;

        var (top, bottom, word) = report.Health.Level switch
        {
            HealthLevel.Good => (GoodColours.Top, GoodColours.Bottom, "Good"),
            HealthLevel.Caution => (CautionColours.Top, CautionColours.Bottom, "Caution"),
            HealthLevel.Bad => (BadColours.Top, BadColours.Bottom, "Bad"),
            _ => (UnknownColours.Top, UnknownColours.Bottom, "Unknown"),
        };

        HealthBox.Background = new LinearGradientBrush(top, bottom, 90);
        HealthWord.Text = word;
        HealthPercent.Text = report.Health.LifePercent is { } percent ? $"{percent} %" : string.Empty;
        HealthDisk.Text = $"{disk.DisplayName}";

        var identity = new List<string> { disk.MediaText, disk.BusTypeName };
        if (!string.IsNullOrWhiteSpace(disk.FirmwareRevision)) identity.Add($"Firmware {disk.FirmwareRevision}");
        if (!string.IsNullOrWhiteSpace(disk.SerialNumber)) identity.Add($"Serial {disk.SerialNumber.Trim()}");
        HealthIdentity.Text = string.Join("  |  ", identity.Where(s => !string.IsNullOrWhiteSpace(s) && s != "Unknown"));
        HealthReasons.Text = report.Health.Level is HealthLevel.Caution or HealthLevel.Bad
            ? string.Join(" ", report.Health.Reasons)
            : string.Empty;

        RowList.ItemsSource = report.Rows.Select(r => new DiskInfoRowViewModel(r)).ToList();
        PlaceholderText.Visibility = Visibility.Collapsed;

        ApplyLayout(report.Layout);

        if (report.Notes.Count > 0)
        {
            NotesText.Text = string.Join(" ", report.Notes);
            NotesText.Visibility = Visibility.Visible;
        }
        else
        {
            NotesText.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowReading(string message)
    {
        HealthBox.Background = new LinearGradientBrush(UnknownColours.Top, UnknownColours.Bottom, 90);
        HealthWord.Text = "Reading...";
        HealthPercent.Text = string.Empty;
        HealthDisk.Text = string.Empty;
        HealthIdentity.Text = string.Empty;
        HealthReasons.Text = string.Empty;
        RowList.ItemsSource = null;
        PlaceholderText.Text = message;
        PlaceholderText.Visibility = Visibility.Visible;
        NotesText.Visibility = Visibility.Collapsed;
        ClearLayout();
    }

    private void ShowFailure(string message)
    {
        HealthBox.Background = new LinearGradientBrush(UnknownColours.Top, UnknownColours.Bottom, 90);
        HealthWord.Text = "Unknown";
        PlaceholderText.Text = message;
        PlaceholderText.Visibility = Visibility.Visible;
    }

    private void ClearLayout()
    {
        PartitionStrip.Children.Clear();
        PartitionStrip.ColumnDefinitions.Clear();
        LayoutName.Text = string.Empty;
        LayoutType.Text = string.Empty;
        LayoutSize.Text = string.Empty;
        LayoutStatus.Text = string.Empty;
    }

    // Left box: disk name, Basic or Dynamic, size, Online or Offline. Right strip: one block per partition and per
    // stretch of unallocated space, left to right in disk order, each as wide as its share of the disk but never
    // narrower than MinimumBlockShare.
    private void ApplyLayout(DiskLayout? layout)
    {
        ClearLayout();
        if (layout is null)
        {
            LayoutName.Text = _current is null ? "" : $"Disk {_current.Index}";
            LayoutType.Text = "Layout not available";
            return;
        }

        LayoutName.Text = layout.Name;
        LayoutType.Text = $"{layout.TypeText}  ({layout.StyleText})";
        LayoutSize.Text = SizeFormat.Format(layout.SizeBytes);
        LayoutStatus.Text = layout.IsReadOnly ? $"{layout.StatusText}, read only" : layout.StatusText;
        LayoutStatus.Foreground = layout.IsOffline ? AmberBrush : MutedBrush;

        var total = layout.Partitions.Sum(p => (double)p.SizeBytes);
        if (total <= 0) total = 1;

        var column = 0;
        foreach (var part in layout.Partitions)
        {
            var share = Math.Max(part.SizeBytes / total, MinimumBlockShare);
            PartitionStrip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(share, GridUnitType.Star) });

            var block = BuildBlock(part);
            Grid.SetColumn(block, column++);
            PartitionStrip.Children.Add(block);
        }
    }

    private static Border BuildBlock(PartitionItem part)
    {
        var warn = part.StatusText.StartsWith("Warning", StringComparison.Ordinal) ||
                   part.StatusText.StartsWith("Unhealthy", StringComparison.Ordinal);

        var text = new StackPanel { Margin = new Thickness(8, 12, 8, 4) };
        text.Children.Add(new TextBlock
        {
            Text = part.Title,
            Foreground = Brushes.White,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        text.Children.Add(new TextBlock
        {
            Text = part.FileSystemText,
            Foreground = MutedBrush,
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        text.Children.Add(new TextBlock
        {
            Text = part.StatusText,
            Foreground = warn ? AmberBrush : MutedBrush,
            FontSize = 10.5,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 44,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var strip = new Border
        {
            Height = 6,
            Background = part.IsUnallocated ? UnallocatedStrip : AllocatedStrip,
            VerticalAlignment = VerticalAlignment.Top,
        };

        var inner = new Grid();
        inner.Children.Add(strip);
        inner.Children.Add(text);

        return new Border
        {
            BorderBrush = BlockBorder,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Background = part.IsUnallocated ? Brushes.Black : BlockBackground,
            Child = inner,
            ToolTip = $"{part.Title}\n{part.FileSystemText}\n{part.StatusText}",
        };
    }

    // ---- actions -----------------------------------------------------------------------------------------------

    private void HelpButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DiskInfoRowViewModel row) return;
        var diskType = _current?.MediaText ?? "Disk";
        DiskInfoExplanationWindow.Show(this, diskType, row.Row);
    }

    private void OpenDiskManagementButton_Click(object sender, RoutedEventArgs e)
    {
        var result = DiskInfoLauncher.OpenDiskManagement();
        if (!result.Success)
        {
            NotesText.Text = result.ErrorMessage ?? "Could not open Disk Management.";
            NotesText.Visibility = Visibility.Visible;
        }
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
        var window = new DiskInfoWindow(owner);
        window.ShowDialog();
    }
}
