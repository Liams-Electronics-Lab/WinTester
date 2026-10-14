using System.Collections.Generic;
using System.Linq;
using static WinTester.Tools.KeyboardTester.VirtualKeys;

namespace WinTester.Tools.KeyboardTester;

public enum KeyboardLayoutKind
{
    Full104,
    Tkl87,
    Laptop75,
    Minimal60
}

// Physical layouts, in "key units" (1u == one standard 1x1 keycap). Coordinates approximate a real ANSI
// board closely enough for a clear on-screen diagram; they are not meant to be pixel-exact against any one
// physical model. Real double-height keys (Numpad +/Enter) are drawn as a single 1u-tall placeholder
// between their two rows rather than a true 2u span — a deliberate v1 simplification (see §7.8's own
// "not yet visually verified" note; this needs a real render pass to see whether that reads clearly enough
// or is worth the extra layout-model complexity of a real row-span).
//
// Labels for keys with two printed legends (number row, brackets, backslash, semicolon, quote,
// comma/period/slash, grave/tilde) show both characters as printed on a real keycap, base then shifted
// (e.g. "1!" for the 1 key, "[{" for the left-bracket key) — FriendlyName carries the more conventional
// plain-English description for anywhere that reads better (HUD, report).
public static class KeyboardLayouts
{
    private static KeyboardKeyDefinition Def(string label, string friendly, int vk, double row, double col,
        double width = 1.0, bool extended = false, bool numpad = false) => new()
    {
        Label = label,
        FriendlyName = friendly,
        VirtualKey = vk,
        IsExtendedKey = extended,
        IsNumpad = numpad,
        Row = row,
        Col = col,
        Width = width
    };

    // ===================== Full (104-key) =====================
    //
    // The canonical layout every other layout below is filtered/derived from. Key set and VK codes match
    // the attached example report's own untested-key enumeration exactly (same 95 non-modifier-duplicated
    // + duplicated-modifier keys, same VK values), since that list is itself ground truth from the tool
    // this is scoped from.
    public static readonly IReadOnlyList<KeyboardKeyDefinition> Full = new List<KeyboardKeyDefinition>
    {
        // Function row. Grouped in 4s (F1-F4, F5-F8, F9-F12) with a 0.5u gap between groups, matching a
        // real ANSI function row; F12 ends at col 15, flush with the main block's right edge (Backspace
        // ends at 15 too), so PrtSc/ScrLk/Pause's own col-15.5 start lines up cleanly after it. Previously
        // F5 (col 5.75) overlapped F4 (ends at col 6) and F9 (col 9.5) overlapped F8 (ends at col 9.75) —
        // both groups were spaced as if contiguous with no inter-group gap. Fixed here.
        Def("Esc", "Escape", Escape, 0, 0),
        Def("F1", "F1", F1, 0, 2), Def("F2", "F2", F2, 0, 3), Def("F3", "F3", F3, 0, 4), Def("F4", "F4", F4, 0, 5),
        Def("F5", "F5", F5, 0, 6.5), Def("F6", "F6", F6, 0, 7.5), Def("F7", "F7", F7, 0, 8.5), Def("F8", "F8", F8, 0, 9.5),
        Def("F9", "F9", F9, 0, 11), Def("F10", "F10", F10, 0, 12), Def("F11", "F11", F11, 0, 13), Def("F12", "F12", F12, 0, 14),
        // Real Print Screen reports its scan code (0x37) with the extended (E0) bit set, same as the
        // rest of the nav cluster — this was wrongly left non-extended here, which meant the one
        // WM_KEYUP Windows does deliver for this key (see KeyboardTesterWindow's WndProc comment on
        // WM_KEYDOWN never arriving for it at all) couldn't match this Id and silently failed to
        // register. Fixed session 64, still not confirmed against real hardware.
        Def("PrtSc", "Print Screen", PrintScreen, 0, 15.5, extended: true),
        Def("ScrLk", "Scroll Lock", ScrollLock, 0, 16.5),
        Def("Pause", "Pause / Break", Pause, 0, 17.5),

        // Number row. Label shows both legends (base char, then shifted char) as printed on a real keycap,
        // e.g. "1!" rather than just "!" — previously Label carried only the shifted glyph, which read as
        // if the base digit/character weren't there at all. Same fix applied below to every other key with
        // two printed legends (brackets, backslash, semicolon, quote, comma/period/slash, grave/tilde).
        Def("`~", "Grave / Tilde", OemTilde, 1.25, 0),
        Def("1!", "1", D1, 1.25, 1), Def("2@", "2", D2, 1.25, 2), Def("3#", "3", D3, 1.25, 3), Def("4$", "4", D4, 1.25, 4),
        Def("5%", "5", D5, 1.25, 5), Def("6^", "6", D6, 1.25, 6), Def("7&", "7", D7, 1.25, 7), Def("8*", "8", D8, 1.25, 8),
        Def("9(", "9", D9, 1.25, 9), Def("0)", "0", D0, 1.25, 10),
        Def("-_", "Minus / Underscore", OemMinus, 1.25, 11), Def("=+", "Equals / Plus", OemPlus, 1.25, 12),
        Def("Backspace", "Backspace", Backspace, 1.25, 13, width: 2),
        Def("Ins", "Insert", Insert, 1.25, 15.5, extended: true),
        Def("Home", "Home", Home, 1.25, 16.5, extended: true),
        Def("PgUp", "Page Up", PageUp, 1.25, 17.5, extended: true),
        Def("Num", "Num Lock", NumLock, 1.25, 19.5, extended: true, numpad: true),
        Def("/", "Numpad Divide", NumpadDivide, 1.25, 20.5, extended: true, numpad: true),
        Def("*", "Numpad Multiply", NumpadMultiply, 1.25, 21.5, numpad: true),

        // Tab row
        Def("Tab", "Tab", Tab, 2.25, 0, width: 1.5),
        Def("Q", "Q", Q, 2.25, 1.5), Def("W", "W", W, 2.25, 2.5), Def("E", "E", E, 2.25, 3.5), Def("R", "R", R, 2.25, 4.5),
        Def("T", "T", T, 2.25, 5.5), Def("Y", "Y", Y, 2.25, 6.5), Def("U", "U", U, 2.25, 7.5), Def("I", "I", I, 2.25, 8.5),
        Def("O", "O", O, 2.25, 9.5), Def("P", "P", P, 2.25, 10.5),
        Def("[{", "Left Bracket", OemOpenBracket, 2.25, 11.5), Def("]}", "Right Bracket", OemCloseBracket, 2.25, 12.5),
        Def("\\|", "Backslash / Pipe", OemPipe, 2.25, 13.5, width: 1.5),
        Def("Del", "Delete", Delete, 2.25, 15.5, extended: true),
        Def("End", "End", End, 2.25, 16.5, extended: true),
        Def("PgDn", "Page Down", PageDown, 2.25, 17.5, extended: true),
        Def("7", "Numpad 7", Numpad7, 2.25, 19.5, numpad: true), Def("8", "Numpad 8", Numpad8, 2.25, 20.5, numpad: true),
        Def("9", "Numpad 9", Numpad9, 2.25, 21.5, numpad: true),
        Def("-", "Numpad Minus", NumpadSubtract, 2.25, 22.5, numpad: true),

        // Caps row
        Def("Caps Lock", "Caps Lock", CapsLock, 3.25, 0, width: 1.75),
        Def("A", "A", A, 3.25, 1.75), Def("S", "S", S, 3.25, 2.75), Def("D", "D", D, 3.25, 3.75), Def("F", "F", F, 3.25, 4.75),
        Def("G", "G", G, 3.25, 5.75), Def("H", "H", H, 3.25, 6.75), Def("J", "J", J, 3.25, 7.75), Def("K", "K", K, 3.25, 8.75),
        Def("L", "L", L, 3.25, 9.75),
        Def(";:", "Semicolon / Colon", OemSemicolon, 3.25, 10.75), Def("'\"", "Quote", OemQuote, 3.25, 11.75),
        Def("Enter", "Enter", Enter, 3.25, 12.75, width: 2.25),
        Def("4", "Numpad 4", Numpad4, 3.25, 19.5, numpad: true), Def("5", "Numpad 5", Numpad5, 3.25, 20.5, numpad: true),
        Def("6", "Numpad 6", Numpad6, 3.25, 21.5, numpad: true),
        Def("+", "Numpad Plus", NumpadAdd, 3.25, 22.5, numpad: true),

        // Shift row
        Def("Shift", "Left Shift", LShift, 4.25, 0, width: 2.25),
        Def("Z", "Z", Z, 4.25, 2.25), Def("X", "X", X, 4.25, 3.25), Def("C", "C", C, 4.25, 4.25), Def("V", "V", V, 4.25, 5.25),
        Def("B", "B", B, 4.25, 6.25), Def("N", "N", N, 4.25, 7.25), Def("M", "M", M, 4.25, 8.25),
        Def(",<", "Comma / Less-Than", OemComma, 4.25, 9.25), Def(".>", "Period / Greater-Than", OemPeriod, 4.25, 10.25),
        Def("/?", "Slash / Question", OemSlash, 4.25, 11.25),
        Def("Shift", "Right Shift", RShift, 4.25, 12.25, width: 2.75),
        Def("\u25B2", "Up Arrow", Up, 4.25, 16.5, extended: true),
        Def("1", "Numpad 1", Numpad1, 4.25, 19.5, numpad: true), Def("2", "Numpad 2", Numpad2, 4.25, 20.5, numpad: true),
        Def("3", "Numpad 3", Numpad3, 4.25, 21.5, numpad: true),
        Def("Enter", "Numpad Enter", Enter, 4.75, 22.5, extended: true, numpad: true),

        // Bottom row
        Def("Ctrl", "Left Ctrl", LCtrl, 5.25, 0, width: 1.25),
        Def("Win", "Left Windows", LWin, 5.25, 1.25, width: 1.25, extended: true),
        Def("Alt", "Left Alt", LAlt, 5.25, 2.5, width: 1.25),
        Def("Space", "Space", Space, 5.25, 3.75, width: 6.25),
        Def("Alt", "Right Alt", RAlt, 5.25, 10, width: 1.25, extended: true),
        Def("Win", "Right Windows", RWin, 5.25, 11.25, width: 1.25, extended: true),
        Def("Menu", "Menu / Application", Apps, 5.25, 12.5, width: 1.25, extended: true),
        Def("Ctrl", "Right Ctrl", RCtrl, 5.25, 13.75, width: 1.25, extended: true),
        Def("\u25C4", "Left Arrow", Left, 5.25, 15.5, extended: true),
        Def("\u25BC", "Down Arrow", Down, 5.25, 16.5, extended: true),
        Def("\u25BA", "Right Arrow", Right, 5.25, 17.5, extended: true),
        Def("0", "Numpad 0", Numpad0, 5.25, 19.5, width: 2, numpad: true),
        Def(".", "Numpad Decimal", NumpadDecimal, 5.25, 21.5, numpad: true),
    };

    // Main alphanumeric block's right edge sits at col 15 on every row in Full above; the nav cluster and
    // numpad both start at col 15.5+, so this one constant cleanly separates "main block" from "everything
    // to its right" for every derived layout below.
    private const double NavClusterStartCol = 15.5;

    // ===================== TKL (87-key) =====================
    //
    // Full minus the numpad block. The nav/arrow cluster needs no repositioning: it already sits at its own
    // fixed offset from the main block regardless of whether a numpad follows it further right.
    public static readonly IReadOnlyList<KeyboardKeyDefinition> Tkl =
        Full.Where(k => !k.IsNumpad).ToList();

    // ===================== Laptop (75%) =====================
    //
    // TKL's key set with the nav cluster pulled 0.25u closer to the main block — the "clustered navigation"
    // the reference describes for its own Laptop layout, kept small deliberately so it can't overlap the
    // main block's own right edge (col 15).
    public static readonly IReadOnlyList<KeyboardKeyDefinition> Laptop = Tkl
        .Select(k => k.Col >= NavClusterStartCol ? k with { Col = k.Col - 0.25 } : k)
        .ToList();

    // ===================== Minimal (60%) =====================
    //
    // Alphanumeric cluster + modifiers only: no function row, no nav cluster, no arrows, no numpad — matches
    // the reference's own "compact alphanumeric cluster" description for its Minimal layout. Derived
    // directly from Full (not Laptop) so the col cutoff stays the same NavClusterStartCol constant rather
    // than needing to track Laptop's shifted coordinates. Rows shifted up by one row to fill the space the
    // dropped function row leaves.
    public static readonly IReadOnlyList<KeyboardKeyDefinition> Minimal = Full
        .Where(k => k.Row > 0 && k.Col < NavClusterStartCol)
        .Select(k => k with { Row = k.Row - 1.25 })
        .ToList();

    public static IReadOnlyList<KeyboardKeyDefinition> For(KeyboardLayoutKind kind) => kind switch
    {
        KeyboardLayoutKind.Full104 => Full,
        KeyboardLayoutKind.Tkl87 => Tkl,
        KeyboardLayoutKind.Laptop75 => Laptop,
        KeyboardLayoutKind.Minimal60 => Minimal,
        _ => Full
    };

    public static string DisplayName(KeyboardLayoutKind kind) => kind switch
    {
        KeyboardLayoutKind.Full104 => "Full (104)",
        KeyboardLayoutKind.Tkl87 => "TKL (87)",
        KeyboardLayoutKind.Laptop75 => "Laptop (75%)",
        KeyboardLayoutKind.Minimal60 => "Minimal (60%)",
        _ => kind.ToString()
    };
}
