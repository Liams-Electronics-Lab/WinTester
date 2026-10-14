namespace WinTester.App;

// The one place to edit the displayed app version before a build/release: bumped by hand, deliberately
// not derived from the assembly/csproj version, so it's a single obvious literal a technician can find
// and change without hunting through MSBuild properties. Shown in the main window's footer (far right,
// MainWindow.xaml) via MainViewModel.AppVersionDisplay.
public static class AppInfo
{
    public const string Version = "1.60";
}
