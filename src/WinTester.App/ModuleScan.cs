using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WinTester.App;

// S147: which of the tile-backing modules are present. A module's "folder" name (Tools.Activation, Tests.Cpu, ...) is the
// source folder WinTester.<name>; at runtime its compiled output is WinTester.<name>.dll next to the exe, so that file is
// what is checked. Shown on the splash (SplashWindow) and used to grey out the tile of a module that is not there
// (MainViewModel). Only the modules that back a tile are listed; Core, Branding, Sensors and Reporting are not.
public static class ModuleScan
{
    public static readonly IReadOnlyList<string> Folders = new[]
    {
        "Tests.Cpu", "Tests.Gpu", "Tests.Memory", "Tests.Power", "Tests.Storage",
        "Tools.Activation", "Tools.Bitlocker", "Tools.Devices", "Tools.RestorePoints", "Tools.DiskInfo", "Tools.HardwareErrors", "Tools.Fans",
        "Tools.BatteryDrain", "Tools.BatteryCharge",
        "Tools.KeyboardTester", "Tools.MouseTester", "Tools.WebcamMic", "Tools.UsbValidator",
    };

    public static string AssemblyFileFor(string folder) => $"WinTester.{folder}.dll";

    public static bool IsPresent(string folder)
    {
        try { return File.Exists(Path.Combine(AppContext.BaseDirectory, AssemblyFileFor(folder))); }
        catch { return true; }   // an unreadable folder must not grey out every tile
    }

    public static List<string> Missing() => Folders.Where(f => !IsPresent(f)).ToList();
}
