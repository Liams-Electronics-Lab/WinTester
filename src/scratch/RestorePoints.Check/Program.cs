using System.Globalization;
using WinTester.Tools.RestorePoints;

// Logic checks for the pure parts of WinTester.Tools.RestorePoints.
// No WMI, registry or vssadmin calls are made. Exit code 0 means all checks passed.

var total = 0;
var failures = 0;

void Eq<T>(string name, T expected, T actual)
{
    total++;
    if (EqualityComparer<T>.Default.Equals(expected, actual)) return;
    failures++;
    Console.WriteLine($"FAIL  {name}: expected {expected}, got {actual}");
}

void Check(string name, bool ok) => Eq(name, true, ok);

void Near(string name, double expected, double actual, double tolerance = 1e-9)
{
    total++;
    if (Math.Abs(expected - actual) <= tolerance) return;
    failures++;
    Console.WriteLine($"FAIL  {name}: expected {expected}, got {actual}");
}

void InCulture(string tag, Action action)
{
    var previous = CultureInfo.CurrentCulture;
    try
    {
        CultureInfo.CurrentCulture = new CultureInfo(tag);
        action();
    }
    finally
    {
        CultureInfo.CurrentCulture = previous;
    }
}

const long Gb = 1024L * 1024 * 1024;
const long Mb = 1024L * 1024;

// ---------- Defaults ----------
Eq("default interval", 86_400L, RestoreSettings.Defaults.IntervalSeconds);
Eq("default lifetime", 7_776_000L, RestoreSettings.Defaults.LifetimeSeconds);
Eq("default lifetime days", 90, RestoreSettings.Defaults.LifetimeDays);
Near("default max percent", 5.0, RestoreSettings.DefaultMaxPercent);
Near("default max of 100 GB", 5.0 * Gb, RestoreSettings.DefaultMaxBytes(100 * Gb), 1);
Eq("default max of 0", 0L, RestoreSettings.DefaultMaxBytes(0));

// ---------- Time conversions ----------
Eq("seconds 1d 2h 3m 4s", 93_784L, RestoreSettings.ToSeconds(1, 2, 3, 4));
Eq("seconds all zero", 0L, RestoreSettings.ToSeconds(0, 0, 0, 0));
Eq("seconds 24 hours", 86_400L, RestoreSettings.ToSeconds(0, 24, 0, 0));
Eq("split 93784", (1, 2, 3, 4), RestoreSettings.Split(93_784));
Eq("split 86399", (0, 23, 59, 59), RestoreSettings.Split(86_399));
Eq("split 86400", (1, 0, 0, 0), RestoreSettings.Split(86_400));
Eq("split 0", (0, 0, 0, 0), RestoreSettings.Split(0));
Eq("split negative", (0, 0, 0, 0), RestoreSettings.Split(-5));
Eq("split round trip", 123_456L, RestoreSettings.ToSeconds(
    RestoreSettings.Split(123_456).Days, RestoreSettings.Split(123_456).Hours,
    RestoreSettings.Split(123_456).Minutes, RestoreSettings.Split(123_456).Seconds));
Eq("lifetime days from seconds", 7, new RestoreSettings(1, 7 * 86_400L).LifetimeDays);

// ---------- Validation ----------
Check("interval 0 rejected", RestoreSettings.ValidateInterval(0) is not null);
Check("interval 1 accepted", RestoreSettings.ValidateInterval(1) is null);
Check("lifetime 0 rejected", RestoreSettings.ValidateLifetimeDays(0) is not null);
Check("lifetime 1 accepted", RestoreSettings.ValidateLifetimeDays(1) is null);
Check("max 319 MB rejected", RestoreSettings.ValidateMaxBytes(319 * Mb, 100 * Gb) is not null);
Check("max 320 MB accepted", RestoreSettings.ValidateMaxBytes(320 * Mb, 100 * Gb) is null);
Check("max above drive rejected", RestoreSettings.ValidateMaxBytes(2 * Gb, 1 * Gb) is not null);
Check("max equal to drive accepted", RestoreSettings.ValidateMaxBytes(1 * Gb, 1 * Gb) is null);
Check("max with unknown drive size accepted", RestoreSettings.ValidateMaxBytes(2 * Gb, 0) is null);

// ---------- Size conversions ----------
Eq("1 GB to bytes", Gb, RestoreSettings.GbToBytes(1.0));
Eq("1.5 GB to bytes", Gb * 3 / 2, RestoreSettings.GbToBytes(1.5));
Near("bytes to GB", 1.0, RestoreSettings.BytesToGb(Gb));
Eq("5 GB to MB", 5_120L, RestoreSettings.ToResizeMegabytes(5 * Gb));
Eq("320 MB to MB", 320L, RestoreSettings.ToResizeMegabytes(320 * Mb));
Near("percent 5 of 100", 5.0, RestoreSettings.PercentOfDrive(5 * Gb, 100 * Gb));
Near("percent of zero drive", 0.0, RestoreSettings.PercentOfDrive(5 * Gb, 0));

// ---------- Number text ----------
InCulture("en-US", () =>
{
    Eq("format 1.5 GB en-US", "1.5", RestoreSettings.FormatGb(Gb * 3 / 2));
    Eq("format 0 GB en-US", "0.0", RestoreSettings.FormatGb(0));
    Check("parse 1.5 en-US", RestoreSettings.TryParseGb("1.5", out var v) && Math.Abs(v - 1.5) < 1e-9);
});
InCulture("de-DE", () =>
{
    Eq("format 1.5 GB de-DE", "1,5", RestoreSettings.FormatGb(Gb * 3 / 2));
    Check("parse 1,5 de-DE", RestoreSettings.TryParseGb("1,5", out var v) && Math.Abs(v - 1.5) < 1e-9);
    Check("parse 1.5 de-DE falls back", RestoreSettings.TryParseGb("1.5", out var w) && Math.Abs(w - 1.5) < 1e-9);
});
Check("parse empty", !RestoreSettings.TryParseGb("", out _));
Check("parse null", !RestoreSettings.TryParseGb(null, out _));
Check("parse text", !RestoreSettings.TryParseGb("abc", out _));
Check("parse Unlimited", !RestoreSettings.TryParseGb("Unlimited", out _));
Check("parse negative", !RestoreSettings.TryParseGb("-1", out _));
Check("parse infinity", !RestoreSettings.TryParseGb("Infinity", out _));
Check("parse padded", RestoreSettings.TryParseGb("  2  ", out var padded) && Math.Abs(padded - 2) < 1e-9);

// ---------- Restore point types ----------
Eq("type 0", "Application Install", RestorePointTypes.NameOf(0));
Eq("type 1", "Application Uninstall", RestorePointTypes.NameOf(1));
Eq("type 10", "Device Driver Install", RestorePointTypes.NameOf(10));
Eq("type 12", "Modify Settings", RestorePointTypes.NameOf(12));
Eq("type 13", "Cancelled Operation", RestorePointTypes.NameOf(13));
Eq("type 16", "Manual", RestorePointTypes.NameOf(16));
Eq("type 18", "Critical Update", RestorePointTypes.NameOf(18));
Eq("type 19 unknown", "Other (19)", RestorePointTypes.NameOf(19));
Eq("type 99 unknown", "Other (99)", RestorePointTypes.NameOf(99));
Eq("create type is Modify Settings", 12u, RestorePointTypes.ModifySettings);
Eq("record type name", "Modify Settings", new RestorePointInfo(1, DateTime.Now, "x", 12).TypeName);

// ---------- WMI date ----------
// Time zone independent: compare in UTC.
Eq("dmtf +540", new DateTime(2026, 10, 11, 0, 30, 15, DateTimeKind.Utc),
    RestorePointInfo.ParseWmiDate("20261011093015.000000+540").ToUniversalTime());
Eq("dmtf -300", new DateTime(2026, 10, 11, 14, 30, 15, DateTimeKind.Utc),
    RestorePointInfo.ParseWmiDate("20261011093015.000000-300").ToUniversalTime());
Eq("dmtf +000", new DateTime(2026, 10, 11, 9, 30, 15, DateTimeKind.Utc),
    RestorePointInfo.ParseWmiDate("20261011093015.000000+000").ToUniversalTime());
Eq("dmtf without offset", new DateTime(2026, 10, 11, 9, 30, 15),
    RestorePointInfo.ParseWmiDate("20261011093015"));
Eq("dmtf null", DateTime.MinValue, RestorePointInfo.ParseWmiDate(null));
Eq("dmtf empty", DateTime.MinValue, RestorePointInfo.ParseWmiDate(""));
Eq("dmtf text", DateTime.MinValue, RestorePointInfo.ParseWmiDate("not a date"));
Eq("dmtf too short", DateTime.MinValue, RestorePointInfo.ParseWmiDate("2026101109301"));
Eq("dmtf bad month", DateTime.MinValue, RestorePointInfo.ParseWmiDate("20261345093015.000000+000"));

// ---------- Drive letters (these values reach a command line) ----------
bool Letter(string? text, char expected)
{
    return RestoreConfigService.TryDriveLetter(text, out var letter) && letter == expected;
}

Check("drive C:", Letter("C:", 'C'));
Check("drive C:\\", Letter(@"C:\", 'C'));
Check("drive c", Letter("c", 'C'));
Check("drive padded", Letter(" d: ", 'D'));
Check("drive empty", !RestoreConfigService.TryDriveLetter("", out _));
Check("drive null", !RestoreConfigService.TryDriveLetter(null, out _));
Check("drive two letters", !RestoreConfigService.TryDriveLetter("CC", out _));
Check("drive digit", !RestoreConfigService.TryDriveLetter("1:", out _));
Check("drive path", !RestoreConfigService.TryDriveLetter(@"C:\Windows", out _));
Check("drive injection", !RestoreConfigService.TryDriveLetter("C: /MaxSize=1MB", out _));

Eq("normalize C:", @"C:\", RestorePointService.NormalizeDrive("C:"));
Eq("normalize C:\\", @"C:\", RestorePointService.NormalizeDrive(@"C:\"));
Eq("normalize c", @"c:\", RestorePointService.NormalizeDrive("c"));
Eq("normalize empty", "", RestorePointService.NormalizeDrive(""));

// ---------- SpaceInfo ----------
Check("space unlimited", new SpaceInfo(0, 0, long.MaxValue).IsUnlimited);
Check("space limited", !new SpaceInfo(0, 0, 5 * Gb).IsUnlimited);

// ---------- Scheduled task XML (automatic restore points) ----------
const string TaskNs = "http://schemas.microsoft.com/windows/2004/02/mit/task";

string TaskXml(string settings) =>
    $"<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n<Task version=\"1.2\" xmlns=\"{TaskNs}\">"
    + "<Triggers><TimeTrigger><Enabled>true</Enabled></TimeTrigger></Triggers>"
    + $"<Settings>{settings}</Settings></Task>";

Eq("task enabled", (bool?)true, AutoRestoreService.ParseTaskEnabled(TaskXml("<Enabled>true</Enabled>")));
Eq("task disabled", (bool?)false, AutoRestoreService.ParseTaskEnabled(TaskXml("<Enabled>false</Enabled>")));
Eq("task disabled, any case", (bool?)false, AutoRestoreService.ParseTaskEnabled(TaskXml("<Enabled> False </Enabled>")));
Eq("task flag missing means enabled", (bool?)true, AutoRestoreService.ParseTaskEnabled(TaskXml("<Hidden>false</Hidden>")));
Eq("trigger flag is ignored", (bool?)false, AutoRestoreService.ParseTaskEnabled(TaskXml("<Enabled>false</Enabled>")));
Eq("text before xml", (bool?)false,
    AutoRestoreService.ParseTaskEnabled("\u00ff\u00fe" + TaskXml("<Enabled>false</Enabled>")));
Eq("nul characters", (bool?)false,
    AutoRestoreService.ParseTaskEnabled(string.Join("\0", TaskXml("<Enabled>false</Enabled>").ToCharArray().Select(c => c.ToString()))));
Eq("not xml", (bool?)null, AutoRestoreService.ParseTaskEnabled("ERROR: The system cannot find the path specified."));
Eq("empty", (bool?)null, AutoRestoreService.ParseTaskEnabled(""));
Eq("null", (bool?)null, AutoRestoreService.ParseTaskEnabled(null));
Eq("broken xml", (bool?)null, AutoRestoreService.ParseTaskEnabled("<Task><Settings>"));

// ---------- Task run history and the Idle assessment ----------
var run = AutoRestoreService.ParseTaskRun("LAST 2026-10-01T03:00:00.0000000+09:00\r\nRESULT 0\r\nNEXT 2026-10-13T03:00:00.0000000+09:00\r\n");
Check("run parsed", run is not null);
Eq("run last (UTC)", new DateTime(2026, 9, 30, 18, 0, 0, DateTimeKind.Utc), run!.LastRun!.Value.ToUniversalTime());
Eq("run next (UTC)", new DateTime(2026, 10, 12, 18, 0, 0, DateTimeKind.Utc), run.NextRun!.Value.ToUniversalTime());
Eq("run result", (int?)0, run.LastResult);

var never = AutoRestoreService.ParseTaskRun("LAST none\nRESULT 267011\nNEXT none\n");
Check("never run parsed", never is not null && never.LastRun is null && never.NextRun is null);
Eq("never run result", (int?)267011, never!.LastResult);
Check("run noise only", AutoRestoreService.ParseTaskRun("hello\nworld") is null);
Check("run null", AutoRestoreService.ParseTaskRun(null) is null);
Check("run bad time", AutoRestoreService.ParseTaskRun("LAST yesterday")?.LastRun is null);

var taskOn = new AutoPointInfo(AutoPointStatus.Enabled, string.Empty);
var windowsUpdate = new RestorePointInfo(5, new DateTime(2026, 10, 1), "Windows Update", 17);
var scheduledOld = new RestorePointInfo(3, new DateTime(2026, 9, 1), "Scheduled", RestorePointTypes.SystemCheckpoint);
var scheduledNew = new RestorePointInfo(4, new DateTime(2026, 9, 20), "Scheduled", RestorePointTypes.SystemCheckpoint);

Eq("task on, no points is idle", AutoPointStatus.Idle, AutoRestoreService.Assess(taskOn, Array.Empty<RestorePointInfo>()).Status);
Eq("task on, only Windows Update is idle", AutoPointStatus.Idle, AutoRestoreService.Assess(taskOn, new[] { windowsUpdate }).Status);
Eq("task on, scheduled point is enabled", AutoPointStatus.Enabled, AutoRestoreService.Assess(taskOn, new[] { windowsUpdate, scheduledOld }).Status);
Eq("newest scheduled point wins", (DateTime?)new DateTime(2026, 9, 20),
    AutoRestoreService.Assess(taskOn, new[] { scheduledOld, scheduledNew, windowsUpdate }).LastScheduledPoint);
Check("idle explains itself", AutoRestoreService.Assess(taskOn, Array.Empty<RestorePointInfo>()).Detail.Length > 0);
Eq("task off stays disabled", AutoPointStatus.Disabled,
    AutoRestoreService.Assess(new AutoPointInfo(AutoPointStatus.Disabled, "off"), new[] { scheduledNew }).Status);
Eq("unknown stays unknown", AutoPointStatus.Unknown,
    AutoRestoreService.Assess(new AutoPointInfo(AutoPointStatus.Unknown, "?"), Array.Empty<RestorePointInfo>()).Status);
Eq("system checkpoint type is 7", 7u, RestorePointTypes.SystemCheckpoint);
Check("summary mentions never", new AutoPointInfo(AutoPointStatus.Idle, "x").Summary().Contains("never"));

Console.WriteLine(failures == 0
    ? $"All {total} checks passed."
    : $"{failures} of {total} checks failed.");
return failures == 0 ? 0 : 1;
