using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using WinTester.Core.Bitlocker;
using WinTester.Core.Tpm;
using WinTester.Tools.Bitlocker;

namespace WinTester.App.Controls;

// "BitLocker" tool window; plain code-behind, no MVVM binding. Shows current BitLocker status on the boot
// drive via BitLockerInfoService (Get-BitLockerVolume), a bitlocker_ENabled.png/bitlocker_DISabled.png badge
// with a bold ENABLED/DISABLED headline, a "Show BitLocker Key" button filling a read-only textbox, a
// "Backup BitLocker Key" button (Save As .txt), and an Enable/Disable toggle that confirms first and forces
// a key backup either before disabling or right after enabling. Also reports which TPM specification the
// machine implements (or that it has none), read alongside the volume status on every refresh.
public partial class BitLockerWindow : Window
{
    private BitLockerSnapshot? _snapshot;
    private bool _isBusy;

    // Green for a TPM that BitLocker can actually bind a key to, amber for one that's present but not
    // usable and for a machine with none at all. Amber rather than red for "no TPM": it's a capability the
    // machine lacks, not a fault it has, and a desktop without one is perfectly healthy.
    private static readonly SolidColorBrush TpmGoodBrush = Frozen(0x3C, 0xB0, 0x43);
    private static readonly SolidColorBrush TpmWarnBrush = Frozen(0xE0, 0xA0, 0x30);

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private BitLockerWindow(Window owner)
    {
        InitializeComponent();
        Owner = owner;
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_isBusy) return;
        _isBusy = true;
        SetBusyState(true);
        ActionErrorText.Visibility = Visibility.Collapsed;
        KeyTextBox.Text = "";

        try
        {
            LoadingText.Text = "Reading BitLocker and TPM status...";
            LoadingText.Visibility = Visibility.Visible;
            ContentPanel.Visibility = Visibility.Collapsed;

            // Two independent powershell.exe calls, started together rather than in sequence: neither
            // depends on the other's result, and running them back to back would roughly double the wait
            // before the window shows anything.
            var bitLockerTask = BitLockerInfoService.GetSnapshotAsync();
            var tpmTask = TpmInfoService.GetSnapshotAsync();

            var snapshot = await bitLockerTask;
            _snapshot = snapshot;

            ApplySnapshot(snapshot);
            ApplyTpmSnapshot(await tpmTask);

            ContentPanel.Visibility = Visibility.Visible;
            LoadingText.Visibility = Visibility.Collapsed;
        }
        finally
        {
            _isBusy = false;
            SetBusyState(false);
        }
    }

    private void ApplySnapshot(BitLockerSnapshot snapshot)
    {
        StatusBadgeImage.Source = LoadBadgeImage(snapshot.IsProtectionOn);

        if (snapshot.IsProtectionOn)
        {
            StatusHeadlineText.Text = "BITLOCKER ENABLED";
            StatusHeadlineText.Foreground = new SolidColorBrush(Color.FromRgb(0xD9, 0x3B, 0x3B)); // red
        }
        else
        {
            StatusHeadlineText.Text = "BITLOCKER DISABLED";
            StatusHeadlineText.Foreground = new SolidColorBrush(Color.FromRgb(0x3C, 0xB0, 0x43)); // green
        }

        DetailText.Text =
            $"Drive              : {snapshot.MountPoint}\n" +
            $"Volume Status      : {snapshot.VolumeStatus}\n" +
            $"Protection Status  : {snapshot.ProtectionStatus}\n" +
            $"Encryption Method  : {snapshot.EncryptionMethod}" +
            (string.IsNullOrEmpty(snapshot.ErrorMessage) ? "" : $"\n\nNOTE: {snapshot.ErrorMessage}");

        ToggleProtectionButton.Content = snapshot.IsProtectionOn ? "Disable BitLocker" : "Enable BitLocker";
    }

    private void ApplyTpmSnapshot(TpmSnapshot tpm)
    {
        if (tpm.ErrorMessage is { Length: > 0 } error)
        {
            TpmHeadlineText.Text = "TPM STATUS UNAVAILABLE";
            TpmHeadlineText.Foreground = TpmWarnBrush;
            TpmDetailText.Text = error;
            return;
        }

        TpmHeadlineText.Foreground = tpm.IsUsable ? TpmGoodBrush : TpmWarnBrush;

        if (!tpm.IsPresent)
        {
            TpmHeadlineText.Text = "NO TPM DETECTED";
            // Deliberately not "no TPM installed": a chip disabled in firmware doesn't get enumerated by
            // Windows at all, and from here that's indistinguishable from a machine that has none.
            TpmDetailText.Text =
                "Windows reports no TPM on this machine. If one is expected, check whether it is enabled " +
                "in the BIOS/UEFI (often listed as PTT on Intel or fTPM on AMD).";
            return;
        }

        TpmHeadlineText.Text = tpm.VersionDisplay.ToUpperInvariant() +
                               (tpm.IsUsable ? " - READY" : " - NOT READY");

        var state = tpm.IsUsable
            ? "Ready for BitLocker"
            : $"Not ready ({(tpm.IsEnabled ? "enabled" : "disabled")}, " +
              $"{(tpm.IsActivated ? "activated" : "not activated")})";

        TpmDetailText.Text =
            $"Spec Version       : {tpm.SpecVersion ?? "Unknown"}\n" +
            $"Manufacturer       : {tpm.ManufacturerId ?? "Unknown"}" +
            (tpm.ManufacturerVersion is { Length: > 0 } firmware ? $"  (firmware {firmware})" : "") + "\n" +
            $"Owned              : {(tpm.IsOwned ? "Yes" : "No")}\n" +
            $"State              : {state}";
    }

    // Enable/disable state for the four action buttons, given the current snapshot and whether an
    // operation is in flight. Show/Backup are enabled only when there's a recovery key to show or back up
    // and BitLocker is currently on.
    private void SetBusyState(bool busy)
    {
        var snapshot = _snapshot;
        var hasUsableKey = !busy && snapshot is { ErrorMessage: null, IsProtectionOn: true, HasRecoveryKey: true };

        RefreshButton.IsEnabled = !busy;
        ShowKeyButton.IsEnabled = hasUsableKey;
        BackupKeyButton.IsEnabled = hasUsableKey;
        ToggleProtectionButton.IsEnabled = !busy && snapshot is { ErrorMessage: null };
    }

    // Loads bitlocker_ENabled.png / bitlocker_DISabled.png from vendor/images/, same loose-file-next-to-
    // the-exe convention ActivationWindow's LoadBadgeImage uses for genuine.png/non-genuine.png.
    private static BitmapImage? LoadBadgeImage(bool isProtectionOn)
    {
        var fileName = isProtectionOn ? "bitlocker_ENabled.png" : "bitlocker_DISabled.png";
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

    private void ShowKeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshot is not { HasRecoveryKey: true } snapshot) return;

        KeyTextBox.Text = snapshot.RecoveryPassword;
        KeyTextBox.Focus();
        KeyTextBox.SelectAll();
    }

    private async void BackupKeyButton_Click(object sender, RoutedEventArgs e)
    {
        ActionErrorText.Visibility = Visibility.Collapsed;
        await RunBackupToFileAsync();
    }

    // Builds the recovery-key report and prompts Save As; returns true only if a file was actually written,
    // so ToggleProtectionButton_Click can tell a decline apart from a real save on a backup-gated action.
    private async Task<bool> RunBackupToFileAsync()
    {
        if (_snapshot is not { HasRecoveryKey: true } snapshot)
        {
            ActionErrorText.Text = "No recovery key is available to back up.";
            ActionErrorText.Visibility = Visibility.Visible;
            return false;
        }

        BackupKeyButton.IsEnabled = false;
        try
        {
            var reportText = await Task.Run(() => BitLockerOperationsService.BuildKeyBackupReport(snapshot));

            var dialog = new SaveFileDialog
            {
                Title = "Save BitLocker Recovery Key Backup",
                FileName = $"BitLocker_Recovery_Key_Backup_{DateTime.Now:yyyy-MM-dd_HHmmss}.txt",
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
            BackupKeyButton.IsEnabled = _snapshot is { ErrorMessage: null, IsProtectionOn: true, HasRecoveryKey: true };
        }
    }

    // Enable/disable toggle. Both directions confirm first, then force a recovery-key backup, before
    // disabling, the last chance to grab the key before it stops mattering, or right after enabling, since
    // the key doesn't exist until Enable-BitLocker creates it.
    private async void ToggleProtectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return; // defensive; the modal confirm dialogs below are the real reentrancy guard.
        if (_snapshot is not { ErrorMessage: null } snapshot) return;

        ActionErrorText.Visibility = Visibility.Collapsed;

        if (snapshot.IsProtectionOn)
            await DisableFlowAsync(snapshot);
        else
            await EnableFlowAsync(snapshot);
    }

    private async Task DisableFlowAsync(BitLockerSnapshot snapshot)
    {
        var confirmed = ConfirmDialog.Show(this,
            $"Disable BitLocker on {snapshot.MountPoint}? The drive will be decrypted and this can take " +
            "a long time. Data remains accessible throughout.");
        if (!confirmed) return;

        if (snapshot.HasRecoveryKey)
        {
            // Forced backup before disabling; this is the last point the key still matters.
            var backedUp = await RunBackupToFileAsync();
            if (!backedUp)
            {
                ActionErrorText.Text = "Recovery key backup was not saved. BitLocker was not disabled; " +
                                        "back up the key first, then try again.";
                ActionErrorText.Visibility = Visibility.Visible;
                return;
            }
        }

        _isBusy = true;
        SetBusyState(true);
        BitLockerActionResult result;
        try
        {
            result = await BitLockerOperationsService.DisableAsync();
        }
        finally
        {
            _isBusy = false;
        }

        if (!result.Success)
        {
            SetBusyState(false); // re-enable the buttons; RefreshAsync (below) never runs on this path,
                                  // so nothing else will undo the SetBusyState(true) above.
            ActionErrorText.Text = $"Failed to disable BitLocker:\n{result.Output}";
            ActionErrorText.Visibility = Visibility.Visible;
            return;
        }

        await RefreshAsync();
    }

    private async Task EnableFlowAsync(BitLockerSnapshot snapshot)
    {
        var confirmed = ConfirmDialog.Show(this,
            $"Enable BitLocker on {snapshot.MountPoint} using TPM + recovery password protection? " +
            "You'll be asked to back up the recovery key immediately afterward.");
        if (!confirmed) return;

        _isBusy = true;
        SetBusyState(true);
        BitLockerActionResult result;
        try
        {
            result = await BitLockerOperationsService.EnableAsync();
        }
        finally
        {
            _isBusy = false;
        }

        if (!result.Success)
        {
            SetBusyState(false); // re-enable the buttons; RefreshAsync (below) never runs on this path.
            ActionErrorText.Text = $"Failed to enable BitLocker:\n{result.Output}";
            ActionErrorText.Visibility = Visibility.Visible;
            return;
        }

        // Reload so _snapshot carries the newly-created recovery password, then force the backup prompt.
        await RefreshAsync();

        if (_snapshot is { HasRecoveryKey: true })
        {
            var backedUp = await RunBackupToFileAsync();
            if (!backedUp)
            {
                ActionErrorText.Text = "BitLocker encryption has started, but the recovery key backup was " +
                                        "not saved. Use \"Backup BitLocker Key\" before this drive leaves the shop.";
                ActionErrorText.Visibility = Visibility.Visible;
            }
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
        var window = new BitLockerWindow(owner);
        window.ShowDialog();
    }
}
