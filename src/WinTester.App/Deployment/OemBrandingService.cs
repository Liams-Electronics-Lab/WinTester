using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Microsoft.Win32;

namespace WinTester.App.Deployment;

// What Windows shows under Settings > System > About and the legacy Control Panel > System.
public sealed record OemInfo(
    string Manufacturer, string Model, string SupportPhone, string SupportHours, string SupportUrl, string LogoPath);

public sealed record OemApplyResult(bool HadError, string Summary);

public sealed record OemBackupResult(bool Success, string Message);

// LogoTempPath = a temp .bmp extracted from the zip (caller deletes it); null if the backup had no usable logo.
// Error is set only when nothing could be loaded; Warning is a non-fatal note (e.g. logo present but unreadable).
public sealed record OemLoadResult(OemInfo? Info, string? LogoTempPath, string? Error, string? Warning);

// "OEM Branding" tool backend: reads/writes HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\OEMInformation, converts
// the chosen picture to the 120x120 24-bit .bmp Windows wants, and optionally stages a Push-Button Reset hook
// (C:\Recovery\OEM\ResetConfig.xml + Set-Branding.cmd + Branding.reg + logo) so the branding is re-applied after a reset.
// Needs an elevated process (HKLM, System32, C:\Recovery); WinTester already runs elevated. Empty text fields
// delete their registry value rather than writing an empty string, so "apply" always leaves the machine
// matching the form.
public static class OemBrandingService
{
    private const string OemKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\OEMInformation";
    private const string LogoFileName = "oemlogo.bmp";
    private const string LegacyLogoFileName = "ShopLogo.bmp"; // name used by the first build; cleaned up when we touch the logo
    private const int LogoSize = 120;
    private const string ScriptFileName = "Set-Branding.cmd";
    private const string LegacyScriptFileName = "Set-Branding.ps1";
    private const string RegFileName = "Branding.reg";
    private const string HiveMount = "WTOEM"; // temporary mount point for the target OS SOFTWARE hive inside WinRE

    private static readonly string[] TextValueNames = { "Manufacturer", "Model", "SupportPhone", "SupportHours", "SupportURL" };

    private static string LogoDir => Path.Combine(Environment.SystemDirectory, "Oobe", "Info");
    private static string LogoDestPath => Path.Combine(LogoDir, LogoFileName);

    private static string RecoveryDir =>
        Path.Combine((Environment.GetEnvironmentVariable("SystemDrive") ?? "C:") + Path.DirectorySeparatorChar, "Recovery", "OEM");

    private static RegistryKey OpenHklm64() => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

    // Current values, for pre-filling the form. Never throws; anything unreadable comes back empty.
    public static OemInfo ReadCurrent()
    {
        try
        {
            using var hklm = OpenHklm64();
            using var key = hklm.OpenSubKey(OemKeyPath);
            string Get(string name) => (key?.GetValue(name) as string ?? "").Trim();
            return new OemInfo(Get("Manufacturer"), Get("Model"), Get("SupportPhone"), Get("SupportHours"), Get("SupportURL"), Get("Logo"));
        }
        catch
        {
            return new OemInfo("", "", "", "", "", "");
        }
    }

    // Applies the form. newLogoSourcePath = a picture the operator just browsed to (any format WPF can decode);
    // null leaves the current logo as it is. Runs on a worker thread; never throws, failures come back in Summary.
    // deleteLogo = remove the logo (registry value + our oemlogo.bmp); ignored if a new logo was chosen.
    public static Task<OemApplyResult> ApplyAsync(OemInfo info, string? newLogoSourcePath, bool deleteLogo, bool includeRecovery) =>
        Task.Run(() => Apply(info, newLogoSourcePath, deleteLogo, includeRecovery));

    private static OemApplyResult Apply(OemInfo info, string? newLogoSourcePath, bool deleteLogo, bool includeRecovery)
    {
        var lines = new StringBuilder();
        var hadError = false;
        string? logoForRecovery = null;

        // --- 1. Logo file (System32\Oobe\Info\oemlogo.bmp) ---
        var logoValue = info.LogoPath;
        if (deleteLogo && string.IsNullOrWhiteSpace(newLogoSourcePath))
        {
            logoValue = ""; // registry value is cleared below; the recovery copy is removed with the hook
            try
            {
                if (File.Exists(LogoDestPath)) File.Delete(LogoDestPath); // only ever our own file, never an arbitrary Logo path
                lines.AppendLine("Logo removed.");
            }
            catch (Exception ex)
            {
                hadError = true;
                lines.AppendLine($"Logo file could not be deleted: {ex.Message}");
            }
        }
        else if (!string.IsNullOrWhiteSpace(newLogoSourcePath))
        {
            try
            {
                Directory.CreateDirectory(LogoDir);
                SaveLogoAsBmp(newLogoSourcePath, LogoDestPath);
                logoValue = LogoDestPath;
                lines.AppendLine("Logo converted to 120x120 24-bit BMP and saved.");
            }
            catch (Exception ex)
            {
                hadError = true;
                lines.AppendLine($"Logo failed: {ex.Message}");
                logoValue = ""; // don't point the registry at a file that isn't there
            }
        }
        if (deleteLogo || !string.IsNullOrWhiteSpace(newLogoSourcePath))
            TryDelete(Path.Combine(LogoDir, LegacyLogoFileName)); // superseded first-build file name
        if (!string.IsNullOrWhiteSpace(logoValue) && File.Exists(logoValue))
            logoForRecovery = logoValue;

        // --- 2. Registry ---
        try
        {
            using var hklm = OpenHklm64();
            using var key = hklm.CreateSubKey(OemKeyPath, writable: true)
                            ?? throw new InvalidOperationException("Could not open the OEMInformation key.");
            var values = new[] { info.Manufacturer, info.Model, info.SupportPhone, info.SupportHours, info.SupportUrl };
            for (var i = 0; i < TextValueNames.Length; i++)
                SetOrClear(key, TextValueNames[i], values[i]);
            SetOrClear(key, "Logo", !string.IsNullOrWhiteSpace(logoValue) && File.Exists(logoValue) ? logoValue : "");
            lines.AppendLine("OEM registry values written.");
        }
        catch (Exception ex)
        {
            hadError = true;
            lines.AppendLine($"Registry write failed: {ex.Message}");
        }

        // --- 3. Recovery hook (optional) ---
        if (includeRecovery)
        {
            try
            {
                lines.Append(WriteRecoveryHook(info, logoForRecovery));
            }
            catch (Exception ex)
            {
                hadError = true;
                lines.AppendLine($"Recovery image update failed: {ex.Message}");
            }
        }

        return new OemApplyResult(hadError, lines.ToString().TrimEnd());
    }

    private static void SetOrClear(RegistryKey key, string name, string value)
    {
        var trimmed = (value ?? "").Trim();
        if (trimmed.Length > 0) key.SetValue(name, trimmed, RegistryValueKind.String);
        else key.DeleteValue(name, throwOnMissingValue: false);
    }

    // Stages C:\Recovery\OEM\{Set-Branding.cmd, Branding.reg, oemlogo.bmp, ResetConfig.xml}. An existing ResetConfig.xml
    // that is not ours is copied aside first: it may belong to somebody else's reset customisation and a bad one breaks resets.
    private static string WriteRecoveryHook(OemInfo info, string? logoSource)
    {
        var dir = RecoveryDir;
        Directory.CreateDirectory(dir);
        var sb = new StringBuilder();

        var recoveryLogo = Path.Combine(dir, LogoFileName);
        TryDelete(Path.Combine(dir, LegacyLogoFileName));
        TryDelete(Path.Combine(dir, LegacyScriptFileName)); // PowerShell script from earlier builds; WinRE has no PowerShell
        if (logoSource is not null) File.Copy(logoSource, recoveryLogo, overwrite: true);
        else if (File.Exists(recoveryLogo)) File.Delete(recoveryLogo); // no logo any more: don't leave a stale one

        File.WriteAllText(Path.Combine(dir, RegFileName), BuildBrandingReg(info, logoSource is not null), Encoding.Unicode); // UTF-16 with BOM, as reg.exe expects
        File.WriteAllText(Path.Combine(dir, ScriptFileName), BuildBrandingCmd(logoSource is not null), new UTF8Encoding(false));

        var configPath = Path.Combine(dir, "ResetConfig.xml");
        if (File.Exists(configPath))
        {
            var existing = File.ReadAllText(configPath);
            var ours = existing.Contains("Set-Branding", StringComparison.OrdinalIgnoreCase); // current or earlier WinTester format
            if (!ours)
            {
                var backup = configPath + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Copy(configPath, backup, overwrite: false);
                sb.AppendLine($"Existing ResetConfig.xml backed up to {Path.GetFileName(backup)}.");
            }
        }
        File.WriteAllText(configPath, BuildResetConfig(), new UTF8Encoding(false));

        sb.AppendLine($"Recovery hook written to {dir}.");
        return sb.ToString();
    }

    // Path is resolved by Windows relative to the OEM folder and must be a bare file name; no Param element
    // is written because an empty one is rejected and none is needed.
    private static string BuildResetConfig() =>
$@"<?xml version=""1.0"" encoding=""utf-8""?>
<Reset>
  <Run Phase=""BasicReset_AfterImageApply"">
    <Path>{ScriptFileName}</Path>
    <Duration>2</Duration>
  </Run>
  <Run Phase=""FactoryReset_AfterImageApply"">
    <Path>{ScriptFileName}</Path>
    <Duration>2</Duration>
  </Run>
</Reset>
";

    private static string RegEscape(string s) =>
        (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim().Replace("\\", "\\\\").Replace("\"", "\\\"");

    // Registry import file applied to the target OS SOFTWARE hive once it is loaded under HiveMount; "name"=- deletes a value.
    private static string BuildBrandingReg(OemInfo info, bool hasLogo)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Windows Registry Editor Version 5.00");
        sb.AppendLine();
        sb.AppendLine($@"[HKEY_LOCAL_MACHINE\{HiveMount}\Microsoft\Windows\CurrentVersion\OEMInformation]");

        void Emit(string name, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) sb.AppendLine($"\"{name}\"=-");
            else sb.AppendLine($"\"{name}\"=\"{RegEscape(value)}\"");
        }
        Emit("Manufacturer", info.Manufacturer);
        Emit("Model", info.Model);
        Emit("SupportPhone", info.SupportPhone);
        Emit("SupportHours", info.SupportHours);
        Emit("SupportURL", info.SupportUrl);
        Emit("Logo", hasLogo ? LogoDestPath : "");
        return sb.ToString();
    }

    // Runs inside WinRE after the image is applied: no PowerShell there, so plain cmd plus reg.exe against the offline hive.
    // %TargetOS% is the target Windows folder set by the reset engine. Every failure is logged and the script always exits 0,
    // because a non-zero exit or a failed launch aborts and rolls back the whole reset.
    private static string BuildBrandingCmd(bool hasLogo)
    {
        var lines = new List<string>
        {
            "@echo off",
            "rem Re-applies the shop's OEM branding to the target OS during a Windows reset. Written by WinTester (OEM Branding tool).",
            "setlocal",
            "set \"HERE=%~dp0\"",
            "set \"LOG=%HERE%Set-Branding.log\"",
            "set \"TOS=%TargetOS%\"",
            "if not defined TOS set \"TOS=C:\\Windows\"",
            "echo [%date% %time%] target OS folder: %TOS% > \"%LOG%\"",
        };
        if (hasLogo)
        {
            lines.Add("if not exist \"%TOS%\\System32\\Oobe\\Info\" mkdir \"%TOS%\\System32\\Oobe\\Info\" >> \"%LOG%\" 2>&1");
            lines.Add($"copy /y \"%HERE%{LogoFileName}\" \"%TOS%\\System32\\Oobe\\Info\\{LogoFileName}\" >> \"%LOG%\" 2>&1");
        }
        lines.Add($"reg load HKLM\\{HiveMount} \"%TOS%\\System32\\Config\\SOFTWARE\" >> \"%LOG%\" 2>&1");
        lines.Add("if errorlevel 1 goto done");
        lines.Add($"reg import \"%HERE%{RegFileName}\" >> \"%LOG%\" 2>&1");
        lines.Add($"reg unload HKLM\\{HiveMount} >> \"%LOG%\" 2>&1");
        lines.Add(":done");
        lines.Add("echo [%date% %time%] finished >> \"%LOG%\"");
        lines.Add("exit /b 0");
        return string.Join("\r\n", lines) + "\r\n";
    }

    // ---------------- Backup / load ----------------
    // Backup zip layout: OEMInformation.xml + oemlogo.bmp (only if a logo is set). The XML uses the same
    // <OEMInformation> element and field names as a Windows unattend.xml (Microsoft-Windows-Shell-Setup), so the
    // block can be lifted straight into an answer file; the .bmp is the same file Windows reads from Oobe\Info.
    private const string BackupXmlName = "OEMInformation.xml";
    private static readonly XNamespace UnattendNs = "urn:schemas-microsoft-com:unattend";
    private static readonly XNamespace WcmNs = "http://schemas.microsoft.com/WMIConfig/2002/State";
    private static readonly XNamespace XsiNs = "http://www.w3.org/2001/XMLSchema-instance";

    // Saves what is on this PC right now (registry values + the file the Logo value points at), not the form.
    public static Task<OemBackupResult> BackupAsync(string zipPath) => Task.Run(() => Backup(zipPath));

    private static OemBackupResult Backup(string zipPath)
    {
        var zipStarted = false;
        try
        {
            var current = ReadCurrent();
            var hasLogo = !string.IsNullOrWhiteSpace(current.LogoPath) && File.Exists(current.LogoPath);
            var hasText = !string.IsNullOrWhiteSpace(current.Manufacturer) || !string.IsNullOrWhiteSpace(current.Model)
                          || !string.IsNullOrWhiteSpace(current.SupportPhone) || !string.IsNullOrWhiteSpace(current.SupportHours)
                          || !string.IsNullOrWhiteSpace(current.SupportUrl);
            if (!hasText && !hasLogo)
                return new OemBackupResult(false, "No OEM information is set on this PC, so there is nothing to back up.");

            byte[]? logoBytes = null;
            if (hasLogo)
            {
                // A .bmp is stored byte-for-byte; anything else (rare, but the Logo value can point anywhere) is converted.
                if (string.Equals(Path.GetExtension(current.LogoPath), ".bmp", StringComparison.OrdinalIgnoreCase))
                    logoBytes = File.ReadAllBytes(current.LogoPath);
                else
                {
                    var tmp = Path.Combine(Path.GetTempPath(), "WinTesterOemBackup_" + Guid.NewGuid().ToString("N") + ".bmp");
                    try
                    {
                        ConvertToBmpUnscaled(current.LogoPath, tmp);
                        logoBytes = File.ReadAllBytes(tmp);
                    }
                    finally { TryDelete(tmp); }
                }
            }

            var xml = BuildBackupXml(current, logoBytes is not null);

            if (File.Exists(zipPath)) File.Delete(zipPath); // the Save As dialog already asked about overwriting
            zipStarted = true;
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                var xmlEntry = zip.CreateEntry(BackupXmlName, CompressionLevel.Optimal);
                using (var w = new StreamWriter(xmlEntry.Open(), new UTF8Encoding(false)))
                    w.Write(xml);

                if (logoBytes is not null)
                {
                    var logoEntry = zip.CreateEntry(LogoFileName, CompressionLevel.Optimal);
                    using var o = logoEntry.Open();
                    o.Write(logoBytes, 0, logoBytes.Length);
                }
            }

            return new OemBackupResult(true, logoBytes is not null
                ? $"Backup saved (OEM information and logo): {zipPath}"
                : $"Backup saved (OEM information, no logo set): {zipPath}");
        }
        catch (Exception ex)
        {
            if (zipStarted) TryDelete(zipPath); // don't leave a half-written zip behind
            return new OemBackupResult(false, $"Backup failed: {ex.Message}");
        }
    }

    private static string BuildBackupXml(OemInfo info, bool hasLogo)
    {
        var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            System.Runtime.InteropServices.Architecture.X86 => "x86",
            _ => "amd64"
        };

        var oem = new XElement(UnattendNs + "OEMInformation");
        void Add(string name, string value)
        {
            if (!string.IsNullOrWhiteSpace(value)) oem.Add(new XElement(UnattendNs + name, value.Trim()));
        }
        Add("Manufacturer", info.Manufacturer);
        Add("Model", info.Model);
        Add("SupportPhone", info.SupportPhone);
        Add("SupportHours", info.SupportHours);
        Add("SupportURL", info.SupportUrl);
        if (hasLogo) Add("Logo", LogoDestPath); // where Windows expects it; on load the bundled .bmp is used instead

        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(UnattendNs + "unattend",
                new XAttribute(XNamespace.Xmlns + "wcm", WcmNs),
                new XAttribute(XNamespace.Xmlns + "xsi", XsiNs),
                new XComment(" OEM branding backup written by WinTester. The OEMInformation block matches the unattend.xml schema (specialize pass). "),
                new XElement(UnattendNs + "settings",
                    new XAttribute("pass", "specialize"),
                    new XElement(UnattendNs + "component",
                        new XAttribute("name", "Microsoft-Windows-Shell-Setup"),
                        new XAttribute("processorArchitecture", arch),
                        new XAttribute("publicKeyToken", "31bf3856ad364e35"),
                        new XAttribute("language", "neutral"),
                        new XAttribute("versionScope", "nonSxS"),
                        oem))));
        return doc.Declaration + Environment.NewLine + doc.Root;
    }

    // Reads a backup zip into the shape the form needs. Nothing is written to the machine. The logo, if any, is
    // extracted to a temp .bmp for the caller to preview/stage (and delete when done).
    public static OemLoadResult LoadBackup(string zipPath)
    {
        string? logoTemp = null;
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);

            var xmlEntry = zip.Entries.FirstOrDefault(e => string.Equals(e.Name, BackupXmlName, StringComparison.OrdinalIgnoreCase))
                           ?? zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
            if (xmlEntry is null)
                return new OemLoadResult(null, null, "That zip has no OEM information file (OEMInformation.xml).", null);

            XDocument doc;
            using (var xs = xmlEntry.Open())
                doc = XDocument.Load(xs);

            // Match by local name so a hand-edited file or a different namespace prefix still loads.
            var oem = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "OEMInformation");
            if (oem is null)
                return new OemLoadResult(null, null, "That file doesn't contain an OEMInformation block.", null);

            string Get(string name) =>
                (oem.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value ?? "").Trim();

            var info = new OemInfo(Get("Manufacturer"), Get("Model"), Get("SupportPhone"), Get("SupportHours"), Get("SupportURL"), "");

            string? warning = null;
            var logoEntry = zip.Entries.FirstOrDefault(e => string.Equals(e.Name, LogoFileName, StringComparison.OrdinalIgnoreCase))
                            ?? zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase));
            if (logoEntry is not null)
            {
                logoTemp = Path.Combine(Path.GetTempPath(), "WinTesterOemLoad_" + Guid.NewGuid().ToString("N") + ".bmp");
                logoEntry.ExtractToFile(logoTemp, overwrite: true);
                if (LoadPreview(logoTemp) is null)
                {
                    TryDelete(logoTemp);
                    logoTemp = null;
                    warning = "The logo in the backup couldn't be read as an image, so it was skipped.";
                }
            }

            return new OemLoadResult(info, logoTemp, null, warning);
        }
        catch (Exception ex)
        {
            if (logoTemp is not null) TryDelete(logoTemp);
            return new OemLoadResult(null, null, $"Couldn't read that backup: {ex.Message}", null);
        }
    }

    // Full-size conversion to .bmp (no resize/pad) for backing up a non-bmp logo exactly as it is.
    private static void ConvertToBmpUnscaled(string sourcePath, string destPath)
    {
        var src = new BitmapImage();
        src.BeginInit();
        src.CacheOption = BitmapCacheOption.OnLoad;
        src.UriSource = new Uri(sourcePath, UriKind.Absolute);
        src.EndInit();
        src.Freeze();

        var encoder = new BmpBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(src, PixelFormats.Bgr24, null, 0)));
        using var fs = File.Create(destPath);
        encoder.Save(fs);
    }

    public static void TryDelete(string? path)
    {
        try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); } catch { }
    }

    // Fits the picture inside 120x120 (aspect kept, centred on white: BMP-24 has no alpha) and writes a
    // 24-bit .bmp, the format/size the OEMInformation Logo value expects.
    public static void SaveLogoAsBmp(string sourcePath, string destPath)
    {
        var src = new BitmapImage();
        src.BeginInit();
        src.CacheOption = BitmapCacheOption.OnLoad;
        src.UriSource = new Uri(sourcePath, UriKind.Absolute);
        src.EndInit();
        src.Freeze();

        var scale = Math.Min((double)LogoSize / src.PixelWidth, (double)LogoSize / src.PixelHeight);
        double w = src.PixelWidth * scale, h = src.PixelHeight * scale;

        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, LogoSize, LogoSize));
            dc.DrawImage(src, new Rect((LogoSize - w) / 2, (LogoSize - h) / 2, w, h));
        }

        var rtb = new RenderTargetBitmap(LogoSize, LogoSize, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var bgr24 = new FormatConvertedBitmap(rtb, PixelFormats.Bgr24, null, 0);

        var encoder = new BmpBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bgr24));
        using var fs = File.Create(destPath);
        encoder.Save(fs);
    }

    // For the preview box: any decodable picture, loaded fully into memory so no file lock is held.
    public static BitmapImage? LoadPreview(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }
}
