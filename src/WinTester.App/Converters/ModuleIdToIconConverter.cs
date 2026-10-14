using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace WinTester.App.Converters;

// Loads raster PNG tile icons (cpu/gpu/hdd/ram/power) from vendor/icons/ next to the exe, the same loose-
// file, rebuild-free swap posture as brand.json and the vendored test-engine binaries. Caches each
// BitmapImage since the icon set is small and fixed. Unmapped or not-yet-wired ids (e.g. "battery") fall
// back to cpu.png.
public sealed class ModuleIdToIconConverter : IValueConverter
{
    private static readonly Dictionary<string, string> IdToFileName = new()
    {
        ["cpu"] = "cpu.png",
        ["gpu"] = "gpu.png",
        ["storage"] = "hdd.png",
        ["memory"] = "ram.png",
        ["power"] = "power.png",
    };

    // Keyed by file name so repeated bindings don't re-decode the same PNG; fixed five-file set, no eviction needed.
    private static readonly Dictionary<string, BitmapImage> Cache = new();

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        var id = value as string ?? "";
        var fileName = IdToFileName.TryGetValue(id, out var found) ? found : "cpu.png";

        if (Cache.TryGetValue(fileName, out var cached))
            return cached;

        var path = Path.Combine(AppContext.BaseDirectory, "vendor", "icons", fileName);
        if (!File.Exists(path))
            return DependencyProperty.UnsetValue;

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad; // load fully into memory, then release the file handle
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze(); // immutable + cross-thread safe, same reasoning as any other shared static resource

        Cache[fileName] = bitmap;
        return bitmap;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
