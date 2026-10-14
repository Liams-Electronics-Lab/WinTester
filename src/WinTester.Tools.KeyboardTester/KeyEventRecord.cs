using System;

namespace WinTester.Tools.KeyboardTester;

public enum KeyEventDirection
{
    Down,
    Repeat,
    Up
}

// One row in the live event stream / report appendix. Mirrors the attached reference example's own event
// line fields exactly (timestamp, direction, key, VK hex/decimal, scan code hex, hold duration on Up only)
// so the report reads the same way that example does.
public sealed record KeyEventRecord
{
    public required DateTime Timestamp { get; init; }
    public required KeyEventDirection Direction { get; init; }
    public required string Label { get; init; }
    public required int VirtualKey { get; init; }
    public required int ScanCode { get; init; }
    public required bool IsExtendedKey { get; init; }
    public double? HeldMilliseconds { get; init; } // set only on Up events

    // Same (VirtualKey + extended-bit) identity as KeyboardKeyDefinition.Id, so a caller holding just the
    // event record (e.g. the WPF window updating one key's on-screen color after a keystroke) can look the
    // affected key back up without needing a second parallel lookup.
    public string KeyId => IsExtendedKey ? $"{VirtualKey:X2}E" : $"{VirtualKey:X2}";
}
