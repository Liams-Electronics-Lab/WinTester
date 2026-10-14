using System;
using System.Collections.Generic;
using System.Linq;

namespace WinTester.Tools.KeyboardTester;

public enum KeyTestState
{
    Untested,
    Held,    // currently pressed, at least once — verified permanently once released
    Verified // released at least once; stays Verified for the rest of the session even while not held
}

// Immutable snapshot of session totals, handed to the WPF window's HUD bindings and to the report builder —
// same fields the attached reference example's own report header shows (timestamp, layout, keystrokes,
// keys tested/total/percent, peak NKRO).
public sealed record KeyboardTesterSummary
{
    public required DateTime Timestamp { get; init; }
    public required KeyboardLayoutKind Layout { get; init; }
    public required int TotalKeystrokes { get; init; }
    public required int KeysTestedCount { get; init; }
    public required int KeysTotalCount { get; init; }
    public required int PeakNkro { get; init; }
    public double PercentTested => KeysTotalCount == 0 ? 0 : 100.0 * KeysTestedCount / KeysTotalCount;
}

// Framework-agnostic session state: tracks per-key tested/held status, the live event log, and
// keystroke/NKRO totals. The WPF window's window-message hook (see KeyboardTesterWindow.WndProc) does
// nothing but translate a raw Win32 key message into (virtualKey, scanCode, isExtended, isRepeat) and hand
// it to RegisterKeyDown/RegisterKeyUp here — everything stateful lives in this class so it can be exercised
// without a real window (and so KeyboardTesterReportBuilder only ever depends on this, not on WPF).
public sealed class KeyboardTesterSession
{
    // Sane upper bound on the full event log kept for the exported report's appendix table — unrelated to
    // LiveEventWindowSize below (the on-screen live view's own, much smaller, rolling window). Nothing in
    // the operator's ask or the reference calls for an unbounded log; this just stops a very long session
    // from producing an unbounded report file.
    private const int MaxStoredEvents = 5000;

    // Matches the attached reference example's own "Latest 48 events" on-screen live view.
    public const int LiveEventWindowSize = 48;

    private readonly Dictionary<string, KeyTestState> _state = new();
    private readonly Dictionary<string, DateTime> _downTimestamps = new();
    private readonly HashSet<string> _heldIds = new();
    private readonly List<KeyEventRecord> _events = new();

    public DateTime SessionStart { get; } = DateTime.Now;
    public KeyboardLayoutKind Layout { get; private set; }
    public IReadOnlyList<KeyboardKeyDefinition> Keys { get; private set; } = Array.Empty<KeyboardKeyDefinition>();
    public int TotalKeystrokes { get; private set; }
    public int PeakNkro { get; private set; }
    public int HeldCount => _heldIds.Count;
    public IReadOnlyList<KeyEventRecord> Events => _events;
    public KeyEventRecord? LastEvent => _events.Count > 0 ? _events[^1] : null;

    public event EventHandler<KeyboardLayoutKind>? LayoutAutoExpanded;

    public KeyboardTesterSession(KeyboardLayoutKind initialLayout)
    {
        SetLayout(initialLayout);
    }

    // Switching layout never discards progress: any key already Held/Verified keeps that status if it
    // reappears in the new layout, so shrinking then re-expanding (or the auto-expand below) never looks
    // like it forgot what was already tested.
    public void SetLayout(KeyboardLayoutKind layout)
    {
        Layout = layout;
        Keys = KeyboardLayouts.For(layout);
        foreach (var k in Keys)
            if (!_state.ContainsKey(k.Id)) _state[k.Id] = KeyTestState.Untested;
    }

    public KeyTestState GetState(string keyId) => _state.TryGetValue(keyId, out var s) ? s : KeyTestState.Untested;

    // vk/scanCode/isExtended come straight from the raw Win32 key message (see KeyboardTesterWindow.WndProc);
    // isRepeat is the message's own "previously down" bit, true for every OS auto-repeat while a key is held.
    public KeyEventRecord RegisterKeyDown(int vk, int scanCode, bool isExtended, bool isRepeat)
    {
        var id = MakeId(vk, isExtended);
        AutoExpandIfUnknown(id);

        var isNewPress = !isRepeat && !_heldIds.Contains(id);
        if (isNewPress)
        {
            _heldIds.Add(id);
            _downTimestamps[id] = DateTime.Now;
            TotalKeystrokes++;
            if (_state.TryGetValue(id, out var current) && current == KeyTestState.Untested)
                _state[id] = KeyTestState.Held;
            if (_heldIds.Count > PeakNkro) PeakNkro = _heldIds.Count;
        }

        var record = new KeyEventRecord
        {
            Timestamp = DateTime.Now,
            Direction = isRepeat ? KeyEventDirection.Repeat : KeyEventDirection.Down,
            Label = FindLabel(id, vk),
            VirtualKey = vk,
            ScanCode = scanCode,
            IsExtendedKey = isExtended
        };
        AppendEvent(record);
        return record;
    }

    public KeyEventRecord RegisterKeyUp(int vk, int scanCode, bool isExtended)
    {
        var id = MakeId(vk, isExtended);
        AutoExpandIfUnknown(id);

        _heldIds.Remove(id);
        double? heldMs = null;
        if (_downTimestamps.TryGetValue(id, out var downAt))
        {
            heldMs = (DateTime.Now - downAt).TotalMilliseconds;
            _downTimestamps.Remove(id);
        }
        // A key only ever needs to be pressed once to be considered tested; Verified is permanent for the
        // rest of the session from here on, regardless of how many more times it's pressed.
        _state[id] = KeyTestState.Verified;

        var record = new KeyEventRecord
        {
            Timestamp = DateTime.Now,
            Direction = KeyEventDirection.Up,
            Label = FindLabel(id, vk),
            VirtualKey = vk,
            ScanCode = scanCode,
            IsExtendedKey = isExtended,
            HeldMilliseconds = heldMs
        };
        AppendEvent(record);
        return record;
    }

    // Clears tested/held state and the event log for a fresh pass, without needing to reopen the window.
    // Keeps the currently-selected layout as-is.
    public void Reset()
    {
        _state.Clear();
        _downTimestamps.Clear();
        _heldIds.Clear();
        _events.Clear();
        TotalKeystrokes = 0;
        PeakNkro = 0;
        foreach (var k in Keys) _state[k.Id] = KeyTestState.Untested;
    }

    public KeyboardTesterSummary BuildSummary() => new()
    {
        Timestamp = DateTime.Now,
        Layout = Layout,
        TotalKeystrokes = TotalKeystrokes,
        KeysTestedCount = Keys.Count(k => GetState(k.Id) == KeyTestState.Verified),
        KeysTotalCount = Keys.Count,
        PeakNkro = PeakNkro
    };

    // Most recent LiveEventWindowSize events, newest first — exactly what the on-screen live stream shows.
    public IReadOnlyList<KeyEventRecord> RecentEvents(int count = LiveEventWindowSize) =>
        _events.Skip(Math.Max(0, _events.Count - count)).Reverse().ToList();

    // If the operator pressed a physical key that isn't part of the currently-displayed (compact) layout —
    // most commonly a numpad key while Minimal/Laptop/TKL is selected, but this covers any key outside
    // the current layout, not just the numpad case the reference specifically calls out — expand to Full so
    // the key that was just pressed becomes visible instead of silently vanishing from the diagram. The
    // window's own key capture (its WndProc hook, see KeyboardTesterWindow) sees every key regardless of
    // which layout is currently drawn, so this check is what actually notices the mismatch.
    private void AutoExpandIfUnknown(string id)
    {
        if (Layout == KeyboardLayoutKind.Full104) return;
        if (Keys.Any(k => k.Id == id)) return;

        SetLayout(KeyboardLayoutKind.Full104);
        LayoutAutoExpanded?.Invoke(this, Layout);
    }

    private static string MakeId(int vk, bool isExtended) => isExtended ? $"{vk:X2}E" : $"{vk:X2}";

    private string FindLabel(string id, int fallbackVk) =>
        KeyboardLayouts.Full.FirstOrDefault(k => k.Id == id)?.Label ?? $"VK_0x{fallbackVk:X2}";

    private void AppendEvent(KeyEventRecord record)
    {
        _events.Add(record);
        if (_events.Count > MaxStoredEvents)
            _events.RemoveRange(0, _events.Count - MaxStoredEvents);
    }
}
