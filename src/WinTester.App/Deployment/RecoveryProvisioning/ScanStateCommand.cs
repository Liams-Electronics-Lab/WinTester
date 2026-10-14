using System.IO;

namespace WinTester.App.Deployment.RecoveryProvisioning;

public sealed record ScanStateCommand(
    string ExePath,
    string WorkingDirectory,
    IReadOnlyList<string> Arguments,
    string ProgressFile,
    string PackagePath)
{
    // One-line form for the log: quotes only around arguments that need them.
    public string CommandLineText => ScanStateArgs.Join(Arguments);
}

// Builds and sanity-checks the ScanState command line (task 3.1). Pure logic, nothing is started here.
//   /apps[:-oeminfo] /config:<c.xml> /ppkg <package> /i:<r.xml> /o /c /v:13 /l:<s.log> /progress:<p.log> /tel:OFF [/drivers]
// Microsoft's return-code list says ScanState rejects command lines over 256 characters (code 12), so every
// per-run file lives in a very short folder (see RunWorkspace) and Build refuses anything near that limit.
public static class ScanStateArgs
{
    public const int MaxArgumentChars = 256;
    private const int SafeArgumentChars = 240;

    // C16: switches that must never be combined with /ppkg + /apps.
    private static readonly string[] ForbiddenSwitches = { "/nocompress", "/hardlink", "/p", "/encrypt" };
    private static readonly string[] ForbiddenPrefixes = { "/target", "/offline" };

    public static ScanStateCommand Build(CaptureOptions options, string usmtDirectory, RunWorkspace work, string packagePath)
    {
        var args = new List<string>
        {
            options.IncludeOemInfo ? "/apps" : "/apps:-oeminfo",
            "/config:" + work.ConfigFile,
            "/ppkg", packagePath,
            "/i:" + work.RulesFile,
            "/o", "/c", "/v:13",
            "/l:" + work.LogFile,
            "/progress:" + work.ProgressFile,
            "/tel:OFF"
        };
        // Third-party INF drivers, no filter. Added last so the rest of the line is unchanged when the option is off.
        if (options.IncludeDrivers) args.Add("/drivers");

        Validate(args);

        return new ScanStateCommand(
            Path.Combine(usmtDirectory, UsmtLocator.ScanStateExe), usmtDirectory, args, work.ProgressFile, packagePath);
    }

    // Throws InvalidOperationException with a readable reason when the list breaks a known rule.
    public static void Validate(IReadOnlyList<string> args)
    {
        if (!args.Any(a => a.StartsWith("/apps", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The ScanState command line has no /apps switch.");
        if (!args.Any(a => a.Equals("/ppkg", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The ScanState command line has no /ppkg switch.");

        foreach (var a in args)
        {
            var name = a.Split(':')[0];
            if (ForbiddenSwitches.Any(f => name.Equals(f, StringComparison.OrdinalIgnoreCase))
                || ForbiddenPrefixes.Any(f => name.StartsWith(f, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"{name} cannot be combined with /apps and /ppkg.");
        }

        var length = Join(args).Length;
        if (length > SafeArgumentChars)
            throw new InvalidOperationException(
                $"The ScanState command line is {length} characters; USMT rejects more than {MaxArgumentChars}.");
    }

    public static string Join(IEnumerable<string> args) => string.Join(" ", args.Select(Quote));

    private static string Quote(string arg) =>
        arg.Length == 0 || arg.IndexOfAny(new[] { ' ', '\t', '"' }) >= 0 ? "\"" + arg.Replace("\"", "\\\"") + "\"" : arg;
}
