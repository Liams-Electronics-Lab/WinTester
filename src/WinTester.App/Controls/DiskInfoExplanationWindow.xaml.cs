using System.Windows;
using System.Windows.Input;
using WinTester.Tools.DiskInfo;

namespace WinTester.App.Controls;

// The popup behind a "?" in the Disk Info table: the explanation of one data point in a read-only text box, and a
// Search button that sends the disk type, the data point name and its current value to a web search.
public partial class DiskInfoExplanationWindow : Window
{
    private readonly string _diskType;
    private readonly SmartRow _row;

    private DiskInfoExplanationWindow(Window owner, string diskType, SmartRow row)
    {
        InitializeComponent();
        Owner = owner;
        _diskType = diskType;
        _row = row;

        var explanation = SmartExplanations.Find(row.Key);
        TitleText.Text = explanation?.Title ?? row.Name;
        ValueText.Text = string.IsNullOrEmpty(row.Detail)
            ? $"{diskType}  |  Current value: {row.Value}"
            : $"{diskType}  |  Current value: {row.Value} ({row.Detail})";

        // An unknown key must not fail the popup; it still offers the search.
        BodyBox.Text = explanation?.Body ?? "No explanation is stored for this data point. Use Search to look it up.";
    }

    private void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        ActionErrorText.Visibility = Visibility.Collapsed;

        var query = SmartExplanations.BuildSearchQuery(_diskType, _row.Name, _row.Value);
        var result = DiskInfoLauncher.OpenUrl(SmartExplanations.BuildSearchUrl(query));
        if (!result.Success)
        {
            ActionErrorText.Text = result.ErrorMessage ?? "Could not open the search.";
            ActionErrorText.Visibility = Visibility.Visible;
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

    // Shows the popup modally over the Disk Info window.
    public static void Show(Window owner, string diskType, SmartRow row)
    {
        var window = new DiskInfoExplanationWindow(owner, diskType, row);
        window.ShowDialog();
    }
}
