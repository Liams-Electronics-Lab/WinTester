namespace WinTester.Tools.KeyboardTester;

// One physical key slot in a layout. Position is in "key units" (1u == one standard 1x1 keycap, the same
// unit convention every mechanical-keyboard layout diagram uses), not pixels — the WPF window converts
// units to pixels at render time via a single scale factor, so resizing the window just changes that one
// number rather than needing per-key recalculation.
//
// Id is the tracking identity used everywhere a key needs to be looked up (tested/held state, event log,
// report tables) — VirtualKey alone isn't unique: Windows already gives Left/Right Ctrl/Alt/Shift/Win their
// own distinct VK codes, but Main Enter and Numpad Enter share VK_RETURN (0x0D) and are only distinguished
// by the scan code's extended (E0) bit. IsExtendedKey carries that bit through from capture to identity, so
// Id (VirtualKey + IsExtendedKey) disambiguates the one real collision in the whole layout the same way the
// external reference this tool is scoped from does (see its own untested-key list, which shows Numpad
// Enter as a distinct synthetic VK for exactly this reason).
public sealed record KeyboardKeyDefinition
{
    public required string Label { get; init; }
    public required string FriendlyName { get; init; }
    public required int VirtualKey { get; init; }
    public bool IsExtendedKey { get; init; }
    public bool IsNumpad { get; init; }
    public required double Row { get; init; }
    public required double Col { get; init; }
    public double Width { get; init; } = 1.0;

    public string Id => IsExtendedKey ? $"{VirtualKey:X2}E" : $"{VirtualKey:X2}";
}

// The small subset of Win32 VK_* constants this tool needs, kept local rather than pulling in a full P/Invoke
// constants file for a handful of values — every one of these is used by KeyboardLayouts below.
public static class VirtualKeys
{
    public const int Escape = 0x1B;
    public const int F1 = 0x70, F2 = 0x71, F3 = 0x72, F4 = 0x73, F5 = 0x74, F6 = 0x75,
        F7 = 0x76, F8 = 0x77, F9 = 0x78, F10 = 0x79, F11 = 0x7A, F12 = 0x7B;
    public const int PrintScreen = 0x2C;
    public const int ScrollLock = 0x91;
    public const int Pause = 0x13;

    public const int OemTilde = 0xC0;
    public const int D0 = 0x30, D1 = 0x31, D2 = 0x32, D3 = 0x33, D4 = 0x34, D5 = 0x35,
        D6 = 0x36, D7 = 0x37, D8 = 0x38, D9 = 0x39;
    public const int OemMinus = 0xBD, OemPlus = 0xBB;
    public const int Backspace = 0x08;

    public const int Tab = 0x09;
    public const int Q = 0x51, W = 0x57, E = 0x45, R = 0x52, T = 0x54, Y = 0x59, U = 0x55,
        I = 0x49, O = 0x4F, P = 0x50;
    public const int OemOpenBracket = 0xDB, OemCloseBracket = 0xDD, OemPipe = 0xDC;
    public const int Enter = 0x0D;

    public const int CapsLock = 0x14;
    public const int A = 0x41, S = 0x53, D = 0x44, F = 0x46, G = 0x47, H = 0x48, J = 0x4A,
        K = 0x4B, L = 0x4C;
    public const int OemSemicolon = 0xBA, OemQuote = 0xDE;

    public const int LShift = 0xA0, RShift = 0xA1;
    public const int Z = 0x5A, X = 0x58, C = 0x43, V = 0x56, B = 0x42, N = 0x4E, M = 0x4D;
    public const int OemComma = 0xBC, OemPeriod = 0xBE, OemSlash = 0xBF;

    public const int LCtrl = 0xA2, RCtrl = 0xA3;
    public const int LWin = 0x5B, RWin = 0x5C;
    public const int LAlt = 0xA4, RAlt = 0xA5;
    public const int Space = 0x20;
    public const int Apps = 0x5D;

    public const int Insert = 0x2D, Delete = 0x2E, Home = 0x24, End = 0x23, PageUp = 0x21, PageDown = 0x22;
    public const int Up = 0x26, Down = 0x28, Left = 0x25, Right = 0x27;

    public const int NumLock = 0x90;
    public const int NumpadDivide = 0x6F, NumpadMultiply = 0x6A, NumpadSubtract = 0x6D, NumpadAdd = 0x6B;
    public const int Numpad0 = 0x60, Numpad1 = 0x61, Numpad2 = 0x62, Numpad3 = 0x63, Numpad4 = 0x64,
        Numpad5 = 0x65, Numpad6 = 0x66, Numpad7 = 0x67, Numpad8 = 0x68, Numpad9 = 0x69;
    public const int NumpadDecimal = 0x6E;
    // Numpad Enter shares Enter's VK (0x0D); IsExtendedKey is what makes its Id distinct from Main Enter.
}
