using System.Windows;
using System.Windows.Input;

namespace WinTester.App.Controls;

// Small reusable Yes/No modal, used by both the per-tile "run standalone?" confirmation and the
// running-state modal's Cancel confirmation.
public partial class ConfirmDialog : Window
{
    private bool _result;

    private ConfirmDialog(Window owner, string message)
    {
        InitializeComponent();
        Owner = owner;
        MessageText.Text = message;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void YesButton_Click(object sender, RoutedEventArgs e)
    {
        _result = true;
        Close();
    }

    private void NoButton_Click(object sender, RoutedEventArgs e)
    {
        _result = false;
        Close();
    }

    // Keyboard fallback: Y/N answer directly and Escape declines, so the dialog can be answered without
    // a working mouse; the click-driven Yes/No buttons remain the primary path.
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Y:
                _result = true;
                e.Handled = true;
                Close();
                break;

            case Key.N:
            case Key.Escape:
                _result = false;
                e.Handled = true;
                Close();
                break;
        }
    }

    // Shows the dialog modally and returns true only if Yes was clicked.
    public static bool Show(Window owner, string message)
    {
        var dialog = new ConfirmDialog(owner, message);
        dialog.ShowDialog();
        return dialog._result;
    }
}
