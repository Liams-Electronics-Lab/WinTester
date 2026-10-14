using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace WinTester.App.Controls;

// Small modal "working..." popup with a Cancel button, same look as ConfirmDialog. Used for work that can
// take long enough (e.g. slmgr /dlv, ~20s) that a silently greyed-out button looks broken. Cancel fires the
// CancellationTokenSource handed to the work; the dialog stays up ("Cancelling...") until the work has
// actually stopped, so a cancelled slmgr is really dead before the caller carries on. It has no other way
// to close: X/Alt+F4/Esc all mean Cancel.
public partial class BusyDialog : Window
{
    private readonly CancellationTokenSource _cts;
    private bool _finished;

    private BusyDialog(Window owner, string message, CancellationTokenSource cts)
    {
        InitializeComponent();
        Owner = owner;
        MessageText.Text = message;
        _cts = cts;
    }

    private void RequestCancel()
    {
        if (_finished || _cts.IsCancellationRequested) return;
        CancelButton.IsEnabled = false;
        MessageText.Text = "Cancelling...";
        _cts.Cancel();
    }

    private void FinishAndClose()
    {
        _finished = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_finished)
        {
            e.Cancel = true; // only the work finishing may close this window
            RequestCancel();
        }
        base.OnClosing(e);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => RequestCancel();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            RequestCancel();
        }
    }

    // Starts `work`, shows the dialog modally until it completes, then returns its result. Throws
    // OperationCanceledException if the operator cancelled (or the work itself was cancelled), and rethrows
    // any exception the work threw.
    public static async Task<T> RunAsync<T>(Window owner, string message, Func<CancellationToken, Task<T>> work)
    {
        using var cts = new CancellationTokenSource();
        var dialog = new BusyDialog(owner, message, cts);

        var task = work(cts.Token);
        _ = task.ContinueWith(_ => dialog.Dispatcher.BeginInvoke(new Action(dialog.FinishAndClose)),
            TaskScheduler.Default);

        dialog.ShowDialog();
        return await task; // completed by now; surfaces cancellation/faults
    }
}
