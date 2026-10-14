using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WinTester.App.Deployment.RecoveryProvisioning;

namespace WinTester.App.Controls;

// 5.5: the gate shown before the Review step when files that are about to be copied already exist at their destinations and
// differ (CustomerFileSet.FindConflicts). Shows every clash with both versions' dates and sizes and what the chosen rule would do,
// like the Windows copy dialog. Continue returns the policy and, for "choose for each file", one choice per listed file.
public partial class CustomerConflictWindow : Window
{
    private sealed class Row
    {
        public CustomerConflict Conflict = null!;
        public TextBlock Result = null!;
        public StackPanel RadioPanel = null!;
        public RadioButton[] Radios = null!;    // 0 = new file, 1 = existing file, 2 = keep both
    }

    private readonly List<Row> _rows = new();
    private readonly CustomerConflictScan _scan;

    public CustomerConflictDecision? Decision { get; private set; }

    private CustomerConflictWindow(CustomerConflictScan scan, CustomerConflictPolicy current)
    {
        InitializeComponent();
        _scan = scan;

        HeaderText.Text = scan.TotalConflicts > 0
            ? $"{scan.TotalConflicts} file(s) already exist at the destinations and are different from the files that will be copied. " +
              "Choose what to do with them. Identical files are left alone. Whatever is at the destination afterwards is what goes into the package."
            : "Some files cannot be copied to the destinations.";

        foreach (var c in scan.Conflicts) AddRow(c);

        if (scan.Blocked.Count > 0)
        {
            BlockedText.Text = "These cannot be copied until you change the item or remove what is in the way:\n" +
                               string.Join("\n", scan.Blocked.Take(8)) + (scan.Blocked.Count > 8 ? $"\n... and {scan.Blocked.Count - 8} more" : "");
            OkButton.IsEnabled = false;
        }

        switch (current)
        {
            case CustomerConflictPolicy.Replace: RbReplace.IsChecked = true; break;
            case CustomerConflictPolicy.KeepBoth: RbBoth.IsChecked = true; break;
            case CustomerConflictPolicy.KeepExisting: RbKeep.IsChecked = true; break;
            case CustomerConflictPolicy.Choose: RbChoose.IsChecked = true; break;
            default: RbNewer.IsChecked = true; break;
        }
    }

    // Null when the operator cancelled.
    public static CustomerConflictDecision? Show(Window owner, CustomerConflictScan scan, CustomerConflictPolicy current)
    {
        var w = new CustomerConflictWindow(scan, current) { Owner = owner };
        return w.ShowDialog() == true ? w.Decision : null;
    }

    private static Brush B(string hex) => (Brush)new BrushConverter().ConvertFromString(hex)!;

    private static string Fmt(DateTime utc) => utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    private void AddRow(CustomerConflict c)
    {
        var row = new Row { Conflict = c };
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };

        panel.Children.Add(new TextBlock
        {
            Text = c.Target, Foreground = B("#FFFFFF"), FontSize = 12, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap
        });

        var newer = c.SourceNewer ? "the new file is newer" : c.TargetNewer ? "the existing file is newer" : "same age, different size";
        panel.Children.Add(new TextBlock
        {
            Text = $"New file       {Fmt(c.SourceUtc)}   {RecoveryFormat.Bytes(c.SourceBytes)}\n" +
                   $"Existing file  {Fmt(c.TargetUtc)}   {RecoveryFormat.Bytes(c.TargetBytes)}   ({newer})",
            Foreground = B("#B7B7BE"), FontSize = 11, FontFamily = new FontFamily("Consolas")
        });

        row.Result = new TextBlock { Foreground = B("#E0B050"), FontSize = 11, TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(row.Result);

        var byDate = c.SourceNewer ? 0 : 1;
        row.Radios = new[]
        {
            NewRadio("Use the new file"), NewRadio("Keep the existing file"), NewRadio("Keep both")
        };
        row.RadioPanel = new StackPanel { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };
        var group = "r" + _rows.Count;
        foreach (var rb in row.Radios)
        {
            rb.GroupName = group;
            row.RadioPanel.Children.Add(rb);
        }
        row.Radios[byDate].IsChecked = true;
        foreach (var rb in row.Radios) rb.Checked += (_, _) => Refresh();
        panel.Children.Add(row.RadioPanel);

        _rows.Add(row);
        RowsPanel.Children.Add(panel);
    }

    private static RadioButton NewRadio(string text) =>
        new() { Content = text, Foreground = Brushes.White, FontSize = 11, Margin = new Thickness(0, 2, 18, 0) };

    private CustomerConflictPolicy CurrentPolicy() =>
        RbNewer.IsChecked == true ? CustomerConflictPolicy.ReplaceIfNewer
        : RbBoth.IsChecked == true ? CustomerConflictPolicy.KeepBoth
        : RbKeep.IsChecked == true ? CustomerConflictPolicy.KeepExisting
        : RbChoose.IsChecked == true ? CustomerConflictPolicy.Choose
        : CustomerConflictPolicy.Replace;

    private static ConflictChoice ChoiceFor(Row r, CustomerConflictPolicy policy) => policy switch
    {
        CustomerConflictPolicy.ReplaceIfNewer => r.Conflict.SourceNewer ? ConflictChoice.UseNew : ConflictChoice.KeepExisting,
        CustomerConflictPolicy.KeepBoth => ConflictChoice.KeepBoth,
        CustomerConflictPolicy.KeepExisting => ConflictChoice.KeepExisting,
        CustomerConflictPolicy.Choose => r.Radios[0].IsChecked == true ? ConflictChoice.UseNew
            : r.Radios[1].IsChecked == true ? ConflictChoice.KeepExisting : ConflictChoice.KeepBoth,
        _ => ConflictChoice.UseNew
    };

    private void Policy_Changed(object sender, RoutedEventArgs e) => Refresh();

    // What each row would do under the selected rule, and the totals.
    private void Refresh()
    {
        if (RbChoose is null || SummaryText is null) return;    // fires while the window is still being built
        var policy = CurrentPolicy();
        ChooseTools.Visibility = policy == CustomerConflictPolicy.Choose ? Visibility.Visible : Visibility.Collapsed;

        int replaced = 0, kept = 0, both = 0;
        foreach (var r in _rows)
        {
            r.RadioPanel.Visibility = policy == CustomerConflictPolicy.Choose ? Visibility.Visible : Visibility.Collapsed;
            var choice = ChoiceFor(r, policy);
            switch (choice)
            {
                case ConflictChoice.UseNew:
                    replaced++;
                    r.Result.Text = "→ the new file replaces the existing one";
                    break;
                case ConflictChoice.KeepExisting:
                    kept++;
                    r.Result.Text = "→ the existing file is kept (the package carries the existing version)";
                    break;
                default:
                    both++;
                    string alt;
                    try { alt = Path.GetFileName(CustomerFileSet.UniqueName(r.Conflict.Target)); }
                    catch (IOException) { alt = "(no free name)"; }
                    r.Result.Text = "→ both are kept; the new file is saved as " + alt;
                    break;
            }
        }

        var more = _scan.TotalConflicts - _rows.Count;
        SummaryText.Text = $"{replaced} replaced, {kept} kept, {both} saved as extra copies" +
                           (more > 0 ? $"; {more} more file(s) are not listed and follow the same rule (when choosing file by file: the newer file wins)" : "") +
                           (_scan.Identical > 0 ? $". {_scan.Identical} identical file(s) are left alone." : ".");
    }

    private void SetAll(int index)
    {
        foreach (var r in _rows) r.Radios[index].IsChecked = true;
    }

    private void AllNew_Click(object sender, RoutedEventArgs e) => SetAll(0);
    private void AllExisting_Click(object sender, RoutedEventArgs e) => SetAll(1);
    private void AllBoth_Click(object sender, RoutedEventArgs e) => SetAll(2);

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        var policy = CurrentPolicy();
        var choices = new Dictionary<string, ConflictChoice>(StringComparer.OrdinalIgnoreCase);
        if (policy == CustomerConflictPolicy.Choose)
            foreach (var r in _rows) choices[r.Conflict.Target] = ChoiceFor(r, policy);
        Decision = new CustomerConflictDecision(policy, choices);
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1) DragMove();
    }
}
