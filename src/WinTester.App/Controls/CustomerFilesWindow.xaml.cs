using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using WinTester.App.Deployment.RecoveryProvisioning;

namespace WinTester.App.Controls;

// 5.5: the list of customer files and folders for the capture (see CustomerFileSet). Rows are built in code. Every row is checked
// with CustomerFileSet.ValidateEach as it changes; OK is refused while a row is red or a size is still being measured.
public partial class CustomerFilesWindow : Window
{
    private sealed class Row
    {
        public string Source = "";
        public bool IsFolder;
        public long Bytes;
        public int Files;
        public bool Measured;
        public bool Partial;
        public TextBox DestBox = null!;
        public TextBlock Info = null!;
        public FrameworkElement Visual = null!;
    }

    private readonly List<Row> _rows = new();
    private readonly string _systemDrive;
    private readonly IReadOnlyList<string> _appFolders;
    private readonly CancellationTokenSource _cts = new();
    private string?[] _errors = Array.Empty<string?>();

    public List<CustomerFileItem>? Result { get; private set; }

    private CustomerFilesWindow(string systemDrive, IReadOnlyList<CustomerFileItem> current, IReadOnlyList<string> appFolders)
    {
        InitializeComponent();
        _systemDrive = systemDrive;
        _appFolders = appFolders;
        foreach (var it in current) AddRow(it.Source, it.IsFolder, it.Destination, it.Bytes, it.Files, measured: true);
        Revalidate();
    }

    // Null when the operator cancelled; otherwise the new list (possibly empty).
    public static List<CustomerFileItem>? Show(Window owner, string systemDrive, IReadOnlyList<CustomerFileItem> current,
        IReadOnlyList<string> appFolders)
    {
        var w = new CustomerFilesWindow(systemDrive, current, appFolders) { Owner = owner };
        return w.ShowDialog() == true ? w.Result : null;
    }

    private static Brush B(string hex) => (Brush)new BrushConverter().ConvertFromString(hex)!;

    private Row AddRow(string source, bool isFolder, string dest, long bytes, int files, bool measured)
    {
        var row = new Row { Source = source, IsFolder = isFolder, Bytes = bytes, Files = files, Measured = measured };

        var grid = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var src = new TextBlock
        {
            Text = (isFolder ? "Folder  " : "File  ") + source,
            Foreground = B("#E1E3E8"), FontSize = 12, TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0)
        };
        Grid.SetColumn(src, 0);

        row.DestBox = new TextBox
        {
            Text = dest, Background = B("#2A2A2E"), Foreground = Brushes.White, BorderBrush = B("#3A3A40"),
            CaretBrush = Brushes.White, FontSize = 12, Padding = new Thickness(4, 3, 4, 3),
            VerticalAlignment = VerticalAlignment.Center, ToolTip = "Destination folder: the item is put inside it under its own name"
        };
        row.DestBox.TextChanged += (_, _) => Revalidate();
        Grid.SetColumn(row.DestBox, 1);

        var browse = new Button
        {
            Content = "...", Width = 30, Height = 24, Margin = new Thickness(6, 0, 0, 0),
            Background = B("#2A2A2E"), Foreground = Brushes.White, BorderThickness = new Thickness(0),
            ToolTip = "Choose the destination folder"
        };
        browse.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog { Title = "Choose the destination folder" };
            if (dlg.ShowDialog(this) == true) row.DestBox.Text = dlg.FolderName;
        };
        Grid.SetColumn(browse, 2);

        var remove = new Button
        {
            Content = "✕", Width = 26, Height = 24, Margin = new Thickness(6, 0, 0, 0),
            Background = B("#2A2A2E"), Foreground = Brushes.White, BorderThickness = new Thickness(0), ToolTip = "Remove this item"
        };
        remove.Click += (_, _) =>
        {
            _rows.Remove(row);
            RowsPanel.Children.Remove(row.Visual);
            Revalidate();
        };
        Grid.SetColumn(remove, 3);

        grid.Children.Add(src);
        grid.Children.Add(row.DestBox);
        grid.Children.Add(browse);
        grid.Children.Add(remove);

        row.Info = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = B("#B7B7BE") };

        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(grid);
        panel.Children.Add(row.Info);
        row.Visual = panel;

        _rows.Add(row);
        RowsPanel.Children.Add(panel);
        return row;
    }

    private List<CustomerFileItem> ToItems() =>
        _rows.Select(r => new CustomerFileItem(r.Source, r.DestBox.Text.Trim(), r.IsFolder, r.Bytes, r.Files)).ToList();

    private static string SizeText(Row r) =>
        !r.Measured ? "Measuring..."
        : $"{r.Files:N0} file(s), {RecoveryFormat.Bytes(r.Bytes)}" + (r.Partial ? " (counted for 30 seconds only; the real size is larger)" : "");

    private void Revalidate()
    {
        ErrorText.Text = "";
        _errors = CustomerFileSet.ValidateEach(ToItems(), _systemDrive, File.Exists, Directory.Exists, _appFolders);
        for (var i = 0; i < _rows.Count; i++)
        {
            var r = _rows[i];
            if (_errors[i] is { } err) { r.Info.Text = err; r.Info.Foreground = B("#E05555"); }
            else { r.Info.Text = SizeText(r); r.Info.Foreground = B("#B7B7BE"); }
        }
        var total = _rows.Sum(r => r.Bytes);
        TotalText.Text = _rows.Count == 0 ? "No items." : $"{_rows.Count} item(s), {RecoveryFormat.Bytes(total)}";
    }

    private void AddFileButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Choose the files to add", CheckFileExists = true, Multiselect = true };
        if (dlg.ShowDialog(this) != true) return;
        foreach (var f in dlg.FileNames) AddSource(f, false);
    }

    private void AddFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose the folders to add", Multiselect = true };
        if (dlg.ShowDialog(this) != true) return;
        foreach (var f in dlg.FolderNames) AddSource(f, true);
    }

    // The destination starts as the item's own parent folder, i.e. "include it where it is". That is right for data outside a
    // user profile; for a profile folder the row turns red and the operator picks a destination.
    private async void AddSource(string path, bool isFolder)
    {
        if (_rows.Count >= CustomerFileSet.MaxItems)
        {
            ErrorText.Text = $"At most {CustomerFileSet.MaxItems} items can be added.";
            return;
        }
        if (_rows.Any(r => string.Equals(r.Source, path, StringComparison.OrdinalIgnoreCase))) return;

        var row = AddRow(path, isFolder, WinPath.Parent(WinPath.Clean(path)) ?? "", 0, 0, measured: false);
        Revalidate();
        try
        {
            var m = await Task.Run(() => CustomerFileSet.Measure(path, isFolder, _cts.Token), _cts.Token);
            row.Files = m.Files;
            row.Bytes = m.Bytes;
            row.Partial = !m.Complete;
            row.Measured = true;
        }
        catch (OperationCanceledException) { return; }
        Revalidate();
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        Revalidate();
        if (_rows.Any(r => !r.Measured)) { ErrorText.Text = "Still measuring sizes. Wait a moment."; return; }
        if (_errors.Any(x => x is not null)) { ErrorText.Text = "Fix the rows shown in red."; return; }
        Result = ToItems();
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1) DragMove();
    }

    protected override void OnClosed(EventArgs e)
    {
        _cts.Cancel();
        base.OnClosed(e);
    }
}
