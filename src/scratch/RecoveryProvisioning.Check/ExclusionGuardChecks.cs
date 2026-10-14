using WinTester.App.Deployment.RecoveryProvisioning;

// Extra paths guard (3.15): lines that would exclude something the package is built from are refused. Called from Program.cs.
public static class ExclusionGuardChecks
{
    private static AppCapturePlan Plan(string name, string folder, params string[] extraFilePatterns)
    {
        var files = new List<string> { folder + @"\* [*]" };
        files.AddRange(extraFilePatterns);
        return new AppCapturePlan(name, "1.0", "Pub", folder, 0, files, Array.Empty<string>(), Array.Empty<string>());
    }

    public static void Run(Action<bool, string> check)
    {
        var foo = Plan("FooApp", @"C:\Program Files\Foo");
        var baz = Plan("BazApp", @"C:\Program Files (x86)\Baz", @"%ProgramFiles(x86)%\Baz\* [*]");
        var runtime = Plan("Runtime", @"C:\Program Files\Runtime Setup", @"C:\Program Files\dotnet\* [*]");

        bool Refused(string line, CaptureOptions o) => ExclusionGuard.Check(line, "C:", ExclusionGuard.ProtectedFor("C:", o)) is not null;
        var plain = new CaptureOptions { IncludeDrivers = false, CopyDesktopShortcuts = false };
        var withApps = new CaptureOptions { IncludeDrivers = false, CopyDesktopShortcuts = false };
        withApps.Apps.AddRange(new[] { foo, baz, runtime });
        var full = new CaptureOptions { IncludeDrivers = true, CopyDesktopShortcuts = true };

        check(Refused(@"C:\", plain) && Refused(@"C:\*", plain), "guard: the whole drive is refused");
        check(Refused(@"C:\Windows\", plain) && Refused(@"C:\Windows\*", plain) && Refused(@"c:\windows\", plain), "guard: the Windows folder is refused (any spelling)");
        check(Refused(@"C:\Windows\System32\", plain), "guard: System32 is refused");
        check(!Refused(@"C:\Windows\Logs\", plain) && !Refused(@"C:\Windows\Temp\", plain) && !Refused(@"C:\Windows\System32\foo\", plain),
            "guard: a narrower folder inside Windows is the operator's choice");
        check(Refused(@"%SystemRoot%\", plain), "guard: a %SystemRoot% line is judged against the system drive");
        check(Refused(@"C:\*\", plain), "guard: a wildcard folder that reaches Windows is refused");
        check(!Refused(@"C:\Users\*\Downloads\", plain) && !Refused(@"C:\Tools\Cache\", plain), "guard: ordinary folders are allowed");
        check(!Refused(@"D:\Windows\", plain) && !Refused(@"D:\", plain), "guard: another drive is not judged (it is not captured)");
        check(!Refused(@"%TEMP%\x", plain), "guard: an unresolved or unrelated %VARIABLE% line is allowed");

        check(Refused(@"C:\Windows\System32\config\", plain) && Refused(@"C:\Windows\System32\config\systemprofile\", plain), "guard: the hive folder and everything in it is refused");
        check(Refused(@"C:\ProgramData\", plain) && !Refused(@"C:\ProgramData\Foo\", plain), "guard: ProgramData itself is refused, a vendor folder in it is allowed");
        check(Refused(@"C:\ProgramData\Microsoft\Windows\Start Menu\", plain) && Refused(@"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\x\", plain),
            "guard: the Start menu folder and everything in it is refused");
        check(Refused(@"C:\Recovery\", plain) && Refused(@"C:\Recovery\Customizations\", plain) && Refused(@"C:\Recovery\OEM\", plain), "guard: the Recovery folder tree is refused");
        check(!Refused(@"C:\Recoveryx\", plain), "guard: a folder that only starts with the same letters is allowed");

        check(!Refused(@"C:\Windows\System32\config\*.log", plain) && !Refused(@"C:\*.tmp", plain), "guard: a junk extension is allowed even in a protected folder");
        check(Refused(@"C:\Windows\*.dll", plain) && Refused(@"C:\Windows\notepad.exe", plain), "guard: other file patterns in the Windows folder are refused");

        check(!Refused(@"C:\Windows\System32\DriverStore\", plain) && Refused(@"C:\Windows\System32\DriverStore\", full), "guard: the driver store is protected only with the drivers option");
        check(!Refused(@"C:\Users\Public\Desktop\", plain) && Refused(@"C:\Users\Public\Desktop\", full) && Refused(@"C:\Users\", full) && !Refused(@"C:\Users\", plain),
            "guard: Public Desktop (and a parent of it) is protected only with the shortcuts option");

        check(Refused(@"C:\Program Files\Foo\", withApps) && Refused(@"C:\Program Files\", withApps), "guard: a ticked app's folder and its parent are refused");
        check(!Refused(@"C:\Program Files\Foo\Cache\", withApps) && !Refused(@"C:\Program Files\Bar\", withApps), "guard: a sub-folder of an app and another app are allowed");
        check(!Refused(@"C:\Program Files\Foo\", plain) && !Refused(@"C:\Program Files\", plain), "guard: with no app ticked those folders are not protected");
        check(Refused(@"C:\Program Files (x86)\Baz\", withApps), "guard: an app under Program Files (x86) is protected (variable form resolved)");
        check(Refused(@"C:\Program Files\dotnet\", withApps), "guard: an extra folder of the app's plan (runtime root) is protected");
        var settingsOnly = new CaptureOptions { Mode = CaptureMode.SettingsOnly };
        settingsOnly.Apps.AddRange(new[] { foo });
        check(!Refused(@"C:\Program Files\Foo\", settingsOnly), "guard: apps are not protected in settings-only mode");

        var msg = ExclusionGuard.Check(@"C:\Windows\", "C:", ExclusionGuard.ProtectedFor("C:", plain));
        check(msg is not null && msg.Contains(@"C:\Windows\") && msg.Contains("Windows system files"), "guard: the message names the line and what it would exclude");
        var msgApp = ExclusionGuard.Check(@"C:\Program Files\Foo\", "C:", ExclusionGuard.ProtectedFor("C:", withApps));
        check(msgApp is not null && msgApp.Contains("FooApp"), "guard: the message names the app");
        check(ExclusionGuard.Violations(new[] { @"C:\Tools\", "", @"C:\Windows\", @"C:\Recovery\" }, "C:", plain).Count == 2, "guard: Violations reports only the refused lines");
        check(ExclusionGuard.Check("not a path", "C:", ExclusionGuard.ProtectedFor("C:", plain)) is null, "guard: a line that does not convert is left to the normal validation");

        var o = new CaptureOptions();
        o.CustomExclusions.Add(@"C:\Windows\");
        o.CustomExclusions.Add(@"C:\Tools\");
        var built = RulesXmlBuilder.Build(o, "C:");
        check(built.Patterns.Contains(@"C:\Tools\* [*]") && !built.Patterns.Contains(@"C:\Windows\* [*]"), "guard: RulesXmlBuilder writes the allowed line and drops the refused one");
    }
}
