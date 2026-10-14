using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace WinTester.App.Converters;

// Loads a raster PNG tile icon from vendor/icons/<file> next to the exe, given a Tab 2 tile's
// StandaloneToolViewModel.IconFileName directly (e.g. "drain.png"): same loose-file, rebuild-free swap
// posture as ModuleIdToIconConverter's Tab 1 icons, brand.json, and the vendored test-engine binaries.
// Unlike ModuleIdToIconConverter there's no id->filename lookup table: Tab 2 has few tiles and each one
// names its own file directly at the MainViewModel.Tab2Tools call site, replacing the vector Icons.xaml
// geometries those tiles used before.
public sealed class IconFileNameToImageConverter : IValueConverter
{
    // Keyed by file name so repeated bindings don't re-decode the same PNG.
    private static readonly Dictionary<string, BitmapImage> Cache = new();

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        var fileName = value as string;
        if (string.IsNullOrEmpty(fileName))
            return DependencyProperty.UnsetValue; // no PNG assigned: StandaloneToolTileControl falls back to the vector Path icon

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
