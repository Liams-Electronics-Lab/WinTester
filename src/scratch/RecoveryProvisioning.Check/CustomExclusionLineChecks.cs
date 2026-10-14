using WinTester.App.Deployment.RecoveryProvisioning;

// Extra paths helper for the Browse buttons (3.15). Called from Program.cs.
public static class CustomExclusionLineChecks
{
    public static void Run(Action<bool, string> check)
    {
        check(CustomExclusionLine.FromFolder(@"C:\Tools\Cache") == @"C:\Tools\Cache\", "extra: a picked folder ends in a backslash");
        check(CustomExclusionLine.FromFolder(@"C:\Tools\Cache\") == @"C:\Tools\Cache\", "extra: a trailing backslash is not doubled");
        check(CustomExclusionLine.FromFolder(@"C:\Tools\App 2.1") == @"C:\Tools\App 2.1\", "extra: a dotted folder name keeps the backslash");
        check(RulesXmlBuilder.TryConvertCustom(CustomExclusionLine.FromFolder(@"C:\Tools\App 2.1")!, out _) == @"C:\Tools\App 2.1\* [*]",
            "extra: a dotted folder converts to a folder pattern, not a file pattern");
        check(CustomExclusionLine.FromFolder(@"D:\") is null && CustomExclusionLine.FromFolder("C:") is null, "extra: a drive root is refused");
        check(CustomExclusionLine.FromFolder("  ") is null, "extra: a blank folder is refused");
        check(CustomExclusionLine.FromFile(@" C:\Tools\app.log ") == @"C:\Tools\app.log", "extra: a picked file is the trimmed full path");
        check(RulesXmlBuilder.TryConvertCustom(CustomExclusionLine.FromFile(@"C:\Tools\app.log"), out _) == @"C:\Tools [app.log]", "extra: a picked file converts to a file pattern");

        check(CustomExclusionLine.Append("", @"C:\A\") == @"C:\A\", "extra: first line goes into an empty box");
        check(CustomExclusionLine.Append(@"C:\A\", @"C:\B\") == "C:\\A\\\r\nC:\\B\\", "extra: a second line is added on its own line");
        check(CustomExclusionLine.Append("C:\\A\\\r\n", @"C:\B\") == "C:\\A\\\r\nC:\\B\\", "extra: a trailing newline does not leave a blank line");
        check(CustomExclusionLine.Append(@"C:\A\", @"c:\a\") == @"C:\A\", "extra: a duplicate line is not added twice (case-insensitive)");
        check(CustomExclusionLine.Append("C:\\X\r\n  C:\\A\\  \r\n", @"C:\A\") == "C:\\X\r\n  C:\\A\\  \r\n", "extra: a duplicate is found through surrounding blanks");
        check(CustomExclusionLine.Append(@"C:\A\", "  ") == @"C:\A\", "extra: a blank line is not added");

        check(CustomExclusionLine.OffSystemDrive(@"D:\Scratch\", "C:") && CustomExclusionLine.OffSystemDrive(@"d:\x", "C:"), "extra: another drive is flagged");
        check(!CustomExclusionLine.OffSystemDrive(@"C:\Tools\", "C:") && !CustomExclusionLine.OffSystemDrive(@"c:\Tools\", "C:"), "extra: the system drive is not flagged");
        check(!CustomExclusionLine.OffSystemDrive(@"%TEMP%\x", "C:"), "extra: a %VARIABLE% line is not judged");
        check(!CustomExclusionLine.OffSystemDrive(@"C:\Tools\", ""), "extra: an empty system drive falls back to C:");
    }
}
