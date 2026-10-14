using System.Diagnostics;
using System.Management;
using System.Runtime.Versioning;

namespace WinTester.Tools.RestorePoints;

public sealed record FixedDrive(string Name, long CapacityBytes);

// Shadow storage numbers for one drive, in bytes.
public sealed record SpaceInfo(long AllocatedBytes, long UsedBytes, long MaxBytes)
{
    // Windows reports an unbounded limit as the largest 64 bit value.
    public bool IsUnlimited => MaxBytes >= long.MaxValue / 2;
}

// Interval and time to live as stored in the registry. A flag is false when the value is not set
// and the Windows default is shown instead.
public sealed record SettingsSnapshot(RestoreSettings Settings, bool IntervalFromSystem, bool LifetimeFromSystem);

// Drives, shadow storage size, and the interval and time to live values.
// Needs administrator rights. Calls block: run them off the UI thread.
[SupportedOSPlatform("windows")]
public static class RestoreConfigService
{
    private const int ResizeTimeoutMs = 60_000;

    public static IReadOnlyList<FixedDrive> GetFixedDrives()
    {
        var drives = new List<FixedDrive>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType == DriveType.Fixed && d.IsReady)
                    drives.Add(new FixedDrive(d.Name.TrimEnd('\\'), d.TotalSize));
            }
            catch (IOException)
            {
                // Skip a drive that cannot be read.
            }
        }

        return drives;
    }

    public static string SystemDrive() =>
        (Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\").TrimEnd('\\');

    // Returns null when the drive has no shadow storage, which means System Restore is off for it.
    public static SpaceInfo? ReadSpace(string drive)
    {
        if (!TryDriveLetter(drive, out var letter)) return null;

        var volumeId = VolumeGuid(letter);
        if (volumeId is null) return null;

        using var searcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT * FROM Win32_ShadowStorage");
        using var results = searcher.Get();
        foreach (var item in results)
        {
            using var mo = (ManagementObject)item;
            var volume = mo["Volume"] as string ?? string.Empty;
            if (volume.Contains(volumeId, StringComparison.OrdinalIgnoreCase))
            {
                return new SpaceInfo(
                    ToLong(mo["AllocatedSpace"]),
                    ToLong(mo["UsedSpace"]),
                    ToLong(mo["MaxSpace"]));
            }
        }

        return null;
    }

    // Sets the maximum space for the drive with vssadmin, in whole megabytes.
    public static OperationResult ResizeMaxSpace(string drive, long maxBytes)
    {
        if (!TryDriveLetter(drive, out var letter))
            return new OperationResult(false, "Select a fixed drive.");

        var target = $"{letter}:";
        return RunVssadmin(
            $"Resize ShadowStorage /For={target} /On={target} /MaxSize={RestoreSettings.ToResizeMegabytes(maxBytes)}MB",
            "Space limit changed.", "Could not change the space limit");
    }

    // Windows can turn protection on without creating a storage limit, and nothing can be saved
    // without one. Creates the limit at the default 5% when the drive has none.
    public static OperationResult EnsureShadowStorage(string drive)
    {
        if (!TryDriveLetter(drive, out var letter))
            return new OperationResult(false, "Select a fixed drive.");

        try
        {
            if (ReadSpace(drive) is not null)
                return new OperationResult(true, "Space limit exists.");
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException)
        {
            return new OperationResult(false, $"Could not read the space limit. {ex.Message}");
        }

        long capacity;
        try
        {
            capacity = new DriveInfo(letter.ToString()).TotalSize;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return new OperationResult(false, $"Could not read the drive size. {ex.Message}");
        }

        var max = Math.Min(capacity, Math.Max(RestoreSettings.MinMaxBytes, RestoreSettings.DefaultMaxBytes(capacity)));
        var target = $"{letter}:";
        return RunVssadmin(
            $"Add ShadowStorage /For={target} /On={target} /MaxSize={RestoreSettings.ToResizeMegabytes(max)}MB",
            "Space limit created.", "Could not create the space limit");
    }

    // Removes the drive's storage association, which is what System Protection off looks like.
    public static OperationResult RemoveShadowStorage(string drive)
    {
        if (!TryDriveLetter(drive, out var letter))
            return new OperationResult(false, "Select a fixed drive.");

        var target = $"{letter}:";
        return RunVssadmin($"Delete ShadowStorage /For={target} /On={target} /Quiet",
            "Space limit removed.", "Could not remove the space limit");
    }

    private static OperationResult RunVssadmin(string arguments, string okText, string failText)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "vssadmin.exe"), arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        try
        {
            using var process = Process.Start(info);
            if (process is null)
                return new OperationResult(false, "Could not start vssadmin.");

            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(ResizeTimeoutMs))
            {
                try { process.Kill(); }
                catch (InvalidOperationException) { }
                return new OperationResult(false, $"{failText}: timed out.");
            }

            if (process.ExitCode == 0)
                return new OperationResult(true, okText);

            var detail = LastLine(errors.Result) ?? LastLine(output.Result);
            return new OperationResult(false, detail is null
                ? $"{failText} (code {process.ExitCode})."
                : $"{failText} (code {process.ExitCode}). {detail}");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new OperationResult(false, $"{failText}. {ex.Message}");
        }
    }

    // Missing values read as the Windows defaults.
    public static RestoreSettings ReadSettings()
    {
        var interval = SrRegistry.ReadDword(SrRegistry.IntervalValue);
        var lifetime = SrRegistry.ReadDword(SrRegistry.LifetimeValue);
        return new RestoreSettings(
            interval ?? RestoreSettings.DefaultIntervalSeconds,
            lifetime ?? RestoreSettings.DefaultLifetimeSeconds);
    }

    // Same as ReadSettings, and says which values are actually set on this computer.
    public static SettingsSnapshot ReadSettingsDetailed()
    {
        var interval = SrRegistry.ReadDword(SrRegistry.IntervalValue);
        var lifetime = SrRegistry.ReadDword(SrRegistry.LifetimeValue);
        return new SettingsSnapshot(
            new RestoreSettings(
                interval ?? RestoreSettings.DefaultIntervalSeconds,
                lifetime ?? RestoreSettings.DefaultLifetimeSeconds),
            interval is not null,
            lifetime is not null);
    }

    public static OperationResult WriteSettings(RestoreSettings settings)
    {
        var problem = RestoreSettings.ValidateInterval(settings.IntervalSeconds)
            ?? RestoreSettings.ValidateLifetimeDays(settings.LifetimeDays);
        if (problem is not null)
            return new OperationResult(false, problem);

        if (settings.IntervalSeconds > uint.MaxValue || settings.LifetimeSeconds > uint.MaxValue)
            return new OperationResult(false, "A time value is too large.");

        try
        {
            SrRegistry.WriteDword(SrRegistry.IntervalValue, (uint)settings.IntervalSeconds);
            SrRegistry.WriteDword(SrRegistry.LifetimeValue, (uint)settings.LifetimeSeconds);
            return new OperationResult(true, "Settings saved.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return new OperationResult(false, $"Could not save the settings. {ex.Message}");
        }
    }

    // "C:", "C:\" and "c" all give 'C'. Anything else fails, so no free text reaches a command line.
    public static bool TryDriveLetter(string? drive, out char letter)
    {
        letter = '\0';
        var text = drive?.Trim().TrimEnd('\\', ':');
        if (text is { Length: 1 } && char.IsAsciiLetter(text[0]))
        {
            letter = char.ToUpperInvariant(text[0]);
            return true;
        }

        return false;
    }

    // "Volume{...}" text that identifies the drive's volume in a Win32_ShadowStorage reference.
    private static string? VolumeGuid(char letter)
    {
        using var searcher = new ManagementObjectSearcher(@"root\cimv2",
            $"SELECT DeviceID FROM Win32_Volume WHERE DriveLetter = '{letter}:'");
        using var results = searcher.Get();
        foreach (var item in results)
        {
            using var mo = (ManagementObject)item;
            var deviceId = mo["DeviceID"] as string;
            var start = deviceId?.IndexOf("Volume{", StringComparison.OrdinalIgnoreCase) ?? -1;
            var end = start < 0 ? -1 : deviceId!.IndexOf('}', start);
            if (start >= 0 && end > start)
                return deviceId![start..(end + 1)];
        }

        return null;
    }

    private static long ToLong(object? value)
    {
        var raw = Convert.ToUInt64(value);
        return raw > long.MaxValue ? long.MaxValue : (long)raw;
    }

    private static string? LastLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
}
