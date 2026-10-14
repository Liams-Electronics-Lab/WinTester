using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using WinTester.Core.Activation;
using WinTester.Tools.Activation;

namespace WinTester.App.Controls;

// "Windows Activation" tool window; plain code-behind, no MVVM binding. Shows current activation status via
// ActivationInfoService, a genuine/non-genuine badge keyed off IsPermanentlyActivated, and two actions:
// Change Product Key (slmgr /ipk + /ato, backup-first prompt) and Backup License (5-method extraction
// report, saved via Save As rather than to the Desktop).
public partial class ActivationWindow : Window
{
    private ActivationSnapshot? _snapshot;
    private bool _isRefreshing;

    private ActivationWindow(Window owner)
    {
        InitializeComponent();
        Owner = owner;
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    // Two-stage load: real cscript.exe/slmgr.vbs latency can hold "Reading activation status..." for up to
    // 30s, but OS name/license status/method/KMS server/product key resolve from local WMI/registry reads
    // in well under a second. Stage 1 awaits GetLocalSnapshotAsync and shows it immediately with a
    // "Checking activation expiry..." placeholder and a provisional non-genuine badge; stage 2 awaits
    // GetExpiryAsync separately and then updates the expiry line and badge. Guarded against re-entrancy
    // (_isRefreshing, RefreshButton disabled) since an overlapping second Refresh click would race the
    // first capture and flash the badge non-genuine → genuine.
    private async Task RefreshAsync()
    {
        if (_isRefreshing) return;
        _isRefreshing = true;
        RefreshButton.IsEnabled = false;
        BackupLicenseButton.IsEnabled = false;
        ChangeKeyButton.IsEnabled = false;
        ActionErrorText.Visibility = Visibility.Collapsed;

        try
        {
            LoadingText.Text = "Reading activation status...";
            LoadingText.Visibility = Visibility.Visible;
            ContentGrid.Visibility = Visibility.Collapsed;

            // --- Stage 1: fast local info, shown as soon as it's ready ---
            var local = await ActivationInfoService.GetLocalSnapshotAsync();
            _snapshot = local;

            if (local.ErrorMessage is not null)
            {
                // Non-Windows / hard failure reading local info at all: nothing left to wait on.
                StatusText.Text = ActivationReportFormatter.Format(local);
                StatusBadgeImage.Source = LoadBadgeImage(false);
                ContentGrid.Visibility = Visibility.Visible;
                LoadingText.Visibility = Visibility.Collapsed;
                return;
            }

            var checking = local with { ExpiryText = "Checking activation expiry (this can take up to ~30s)..." };
            StatusText.Text = ActivationReportFormatter.Format(checking);
            StatusBadgeImage.Source = LoadBadgeImage(false); // provisional: replaced once expiry resolves
            ContentGrid.Visibility = Visibility.Visible;
            LoadingText.Text = "Checking activation expiry...";
            // LoadingText stays visible as a small "still working" note alongside the now-visible ContentGrid.

            // --- Stage 2: the slow slmgr /xpr call ---
            var (expiryText, isPermanent) = await ActivationInfoService.GetExpiryAsync();
            _snapshot = local with { ExpiryText = expiryText, IsPermanentlyActivated = isPermanent };

            StatusText.Text = ActivationReportFormatter.Format(_snapshot);
            StatusBadgeImage.Source = LoadBadgeImage(_snapshot.IsPermanentlyActivated);
            LoadingText.Visibility = Visibility.Collapsed;
        }
        finally
        {
            BackupLicenseButton.IsEnabled = true;
            ChangeKeyButton.IsEnabled = true;
            RefreshButton.IsEnabled = true;
            _isRefreshing = false;
        }
    }

    // Loads genuine.png or non-genuine.png from vendor/images/ (loose file next to the exe, same convention
    // as vendor/icons); dialog resizing comes from the Image control's Width/Stretch="Uniform", not
    // pre-scaling the bitmap. IsPermanentlyActivated decides which image shows, not LicenseStatus, any
    // non-permanently-activated state, including grace periods and errors, gets non-genuine.png.
    private static BitmapImage? LoadBadgeImage(bool isPermanentlyActivated)
    {
        var fileName = isPermanentlyActivated ? "genuine.png" : "non-genuine.png";
        var path = Path.Combine(AppContext.BaseDirectory, "vendor", "images", fileName);
        if (!File.Exists(path)) return null;

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private async void ChangeKeyButton_Click(object sender, RoutedEventArgs e)
    {
        ActionErrorText.Visibility = Visibility.Collapsed;

        var wantsBackup = ConfirmDialog.Show(this,
            "Back up the current activation details before changing the product key?");

        if (wantsBackup)
        {
            var backedUp = await RunBackupToFileAsync();
            if (!backedUp)
            {
                // Backup itself failed or the Save As dialog was cancelled — ask whether to proceed with
                // the key change anyway rather than silently skipping the requested backup.
                var proceedAnyway = ConfirmDialog.Show(this,
                    "Backup was not saved. Continue changing the product key anyway?");
                if (!proceedAnyway) return;
            }
        }

        var newKey = ProductKeyInputWindow.Show(this);
        if (string.IsNullOrWhiteSpace(newKey)) return;

        ChangeKeyButton.IsEnabled = false;
        try
        {
            var result = await ProductKeyOperationsService.ApplyProductKeyAsync(newKey);
            if (!result.Success)
            {
                ActionErrorText.Text = "Product key change failed:\n" + result.InstallOutput +
                                        (string.IsNullOrWhiteSpace(result.ActivateOutput) ? "" : "\n" + result.ActivateOutput);
                ActionErrorText.Visibility = Visibility.Visible;
                return;
            }

            await RefreshAsync(); // reload status so the new key/activation state shows immediately
        }
        finally
        {
            ChangeKeyButton.IsEnabled = true;
        }
    }

    private async void BackupLicenseButton_Click(object sender, RoutedEventArgs e)
    {
        ActionErrorText.Visibility = Visibility.Collapsed;
        await RunBackupToFileAsync();
    }

    // Builds the backup report and prompts Save As; returns true only if a file was actually written, so
    // ChangeKeyButton_Click can tell a decline apart from a real save when deciding whether to nag.
    private async Task<bool> RunBackupToFileAsync()
    {
        BackupLicenseButton.IsEnabled = false;
        try
        {
            string reportText;
            try
            {
                reportText = await BusyDialog.RunAsync(this, "Building backup...",
                    ct => ProductKeyOperationsService.BuildBackupReportAsync(ct));
            }
            catch (OperationCanceledException)
            {
                return false; // operator cancelled; slmgr was already killed
            }

            var dialog = new SaveFileDialog
            {
                Title = "Save Windows Activation Key Backup",
                FileName = $"Windows_Activation_Key_Backup_{DateTime.Now:yyyy-MM-dd_HHmmss}.txt",
                Filter = "Text file (*.txt)|*.txt|All files (*.*)|*.*",
                DefaultExt = ".txt"
            };

            if (dialog.ShowDialog(this) != true) return false;

            File.WriteAllText(dialog.FileName, reportText);
            return true;
        }
        catch (Exception ex)
        {
            ActionErrorText.Text = $"Backup failed: {ex.Message}";
            ActionErrorText.Visibility = Visibility.Visible;
            return false;
        }
        finally
        {
            BackupLicenseButton.IsEnabled = true;
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
        var window = new ActivationWindow(owner);
        window.ShowDialog();
    }
}
