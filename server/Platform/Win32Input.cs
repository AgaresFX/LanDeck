using System.Runtime.InteropServices;
using PadNook.Actions;

namespace PadNook.Platform;

/// <summary>
/// Keyboard injection through SendInput. Regular keys are sent as hardware scan codes
/// (layout independent, like pressing the physical key); media keys as virtual keys.
/// </summary>
internal static class Win32Input
{
    public const ushort VK_MEDIA_NEXT_TRACK = 0xB0;
    public const ushort VK_MEDIA_PREV_TRACK = 0xB1;
    public const ushort VK_MEDIA_STOP = 0xB2;
    public const ushort VK_MEDIA_PLAY_PAUSE = 0xB3;

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_SCANCODE = 0x0008;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    private readonly record struct Stroke(ushort Vk, ushort Scan, bool Extended, bool IsModifier);

    private static Stroke Sc(ushort scan, bool ext = false, bool mod = false) => new(0, scan, ext, mod);
    private static Stroke Vk(ushort vk) => new(vk, 0, false, false);

    // Names follow KeyboardEvent.code (what the webapp records), plus modifier aliases.
    private static readonly Dictionary<string, Stroke> Keys = BuildKeyMap();

    private static Dictionary<string, Stroke> BuildKeyMap()
    {
        var m = new Dictionary<string, Stroke>(StringComparer.OrdinalIgnoreCase)
        {
            // Modifiers
            ["Ctrl"] = Sc(0x1D, mod: true), ["Control"] = Sc(0x1D, mod: true), ["ControlLeft"] = Sc(0x1D, mod: true),
            ["ControlRight"] = Sc(0x1D, true, true),
            ["Alt"] = Sc(0x38, mod: true), ["AltLeft"] = Sc(0x38, mod: true), ["AltRight"] = Sc(0x38, true, true),
            ["Shift"] = Sc(0x2A, mod: true), ["ShiftLeft"] = Sc(0x2A, mod: true), ["ShiftRight"] = Sc(0x36, mod: true),
            ["Win"] = Sc(0x5B, true, true), ["Meta"] = Sc(0x5B, true, true), ["MetaLeft"] = Sc(0x5B, true, true),
            ["MetaRight"] = Sc(0x5C, true, true),

            // Main block
            ["Escape"] = Sc(0x01), ["Minus"] = Sc(0x0C), ["Equal"] = Sc(0x0D), ["Backspace"] = Sc(0x0E),
            ["Tab"] = Sc(0x0F), ["BracketLeft"] = Sc(0x1A), ["BracketRight"] = Sc(0x1B), ["Enter"] = Sc(0x1C),
            ["Semicolon"] = Sc(0x27), ["Quote"] = Sc(0x28), ["Backquote"] = Sc(0x29), ["Backslash"] = Sc(0x2B),
            ["Comma"] = Sc(0x33), ["Period"] = Sc(0x34), ["Slash"] = Sc(0x35), ["Space"] = Sc(0x39),
            ["CapsLock"] = Sc(0x3A), ["IntlBackslash"] = Sc(0x56), ["ScrollLock"] = Sc(0x46),

            // Navigation (extended)
            ["Insert"] = Sc(0x52, true), ["Delete"] = Sc(0x53, true), ["Home"] = Sc(0x47, true), ["End"] = Sc(0x4F, true),
            ["PageUp"] = Sc(0x49, true), ["PageDown"] = Sc(0x51, true),
            ["ArrowUp"] = Sc(0x48, true), ["ArrowDown"] = Sc(0x50, true), ["ArrowLeft"] = Sc(0x4B, true), ["ArrowRight"] = Sc(0x4D, true),
            ["PrintScreen"] = Sc(0x37, true), ["ContextMenu"] = Sc(0x5D, true),

            // Numpad
            ["NumpadMultiply"] = Sc(0x37), ["NumpadSubtract"] = Sc(0x4A), ["NumpadAdd"] = Sc(0x4E),
            ["NumpadDecimal"] = Sc(0x53), ["NumpadDivide"] = Sc(0x35, true), ["NumpadEnter"] = Sc(0x1C, true),
            ["Numpad7"] = Sc(0x47), ["Numpad8"] = Sc(0x48), ["Numpad9"] = Sc(0x49),
            ["Numpad4"] = Sc(0x4B), ["Numpad5"] = Sc(0x4C), ["Numpad6"] = Sc(0x4D),
            ["Numpad1"] = Sc(0x4F), ["Numpad2"] = Sc(0x50), ["Numpad3"] = Sc(0x51), ["Numpad0"] = Sc(0x52),

            // Keys that are better sent as virtual keys
            ["NumLock"] = Vk(0x90), ["Pause"] = Vk(0x13),
            ["MediaPlayPause"] = Vk(0xB3), ["MediaTrackNext"] = Vk(0xB0), ["MediaTrackPrevious"] = Vk(0xB1), ["MediaStop"] = Vk(0xB2),
            ["AudioVolumeMute"] = Vk(0xAD), ["AudioVolumeDown"] = Vk(0xAE), ["AudioVolumeUp"] = Vk(0xAF),
            ["BrowserBack"] = Vk(0xA6), ["BrowserForward"] = Vk(0xA7), ["BrowserRefresh"] = Vk(0xA8), ["BrowserHome"] = Vk(0xAC),
        };

        // Digits: Digit1..Digit9 = 0x02..0x0A, Digit0 = 0x0B
        for (var i = 1; i <= 9; i++) m[$"Digit{i}"] = Sc((ushort)(0x01 + i));
        m["Digit0"] = Sc(0x0B);

        // Letters by physical position (set-1 scan codes)
        var rows = new (string Letters, ushort Start)[] { ("QWERTYUIOP", 0x10), ("ASDFGHJKL", 0x1E), ("ZXCVBNM", 0x2C) };
        foreach (var (letters, start) in rows)
            for (var i = 0; i < letters.Length; i++)
                m[$"Key{letters[i]}"] = Sc((ushort)(start + i));

        // Function keys
        for (var i = 1; i <= 10; i++) m[$"F{i}"] = Sc((ushort)(0x3A + i));
        m["F11"] = Sc(0x57); m["F12"] = Sc(0x58);
        for (var i = 13; i <= 23; i++) m[$"F{i}"] = Sc((ushort)(0x64 + i - 13));
        m["F24"] = Sc(0x76);

        return m;
    }

    private static Stroke Resolve(string name)
    {
        if (Keys.TryGetValue(name, out var s)) return s;
        // Convenience: "A" -> KeyA, "5" -> Digit5
        if (name.Length == 1 && char.IsLetter(name[0]) && Keys.TryGetValue("Key" + char.ToUpperInvariant(name[0]), out s)) return s;
        if (name.Length == 1 && char.IsDigit(name[0]) && Keys.TryGetValue("Digit" + name, out s)) return s;
        throw new ActionException(L.F("Tasto sconosciuto: {0}", name));
    }

    private static INPUT Make(Stroke k, bool up)
    {
        uint flags = 0;
        if (k.Scan != 0) flags |= KEYEVENTF_SCANCODE;
        if (k.Extended) flags |= KEYEVENTF_EXTENDEDKEY;
        if (up) flags |= KEYEVENTF_KEYUP;
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion { ki = new KEYBDINPUT { wVk = k.Scan != 0 ? (ushort)0 : k.Vk, wScan = k.Scan, dwFlags = flags } },
        };
    }

    private static void Send(INPUT[] inputs)
    {
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
        {
            var err = Marshal.GetLastWin32Error();
            PadNook.Core.Log.Error($"SendInput sent {sent}/{inputs.Length}, error {err}, cbSize {Marshal.SizeOf<INPUT>()}");
            throw new ActionException(err == 5
                ? L.T("Windows ha bloccato l'invio dei tasti: il PC è bloccato o è in primo piano una schermata protetta")
                : L.T("Windows ha bloccato l'invio dei tasti (finestra con privilegi di amministratore o schermata protetta?)"));
        }
    }

    public static async Task SendChordAsync(IReadOnlyList<string> names)
    {
        var strokes = names.Select(Resolve).ToList();
        // Modifiers first, then the other keys in the given order.
        var ordered = strokes.Where(s => s.IsModifier).Concat(strokes.Where(s => !s.IsModifier)).ToList();
        Send(ordered.Select(k => Make(k, false)).ToArray());
        await Task.Delay(15); // some apps ignore zero-length presses
        Send(Enumerable.Reverse(ordered).Select(k => Make(k, true)).ToArray());
    }

    private const uint KEYEVENTF_UNICODE = 0x0004;

    private static INPUT Unicode(char ch, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0) } },
    };

    /// <summary>Types text as Unicode characters (independent of the keyboard layout). Newlines press Enter.</summary>
    public static async Task TypeTextAsync(string text)
    {
        var batch = new List<INPUT>();
        foreach (var ch in text.Replace("\r\n", "\n"))
        {
            if (ch == '\r') continue;
            if (ch == '\n' || ch == '\t')
            {
                var k = Keys[ch == '\n' ? "Enter" : "Tab"];
                batch.Add(Make(k, false));
                batch.Add(Make(k, true));
            }
            else
            {
                batch.Add(Unicode(ch, false));
                batch.Add(Unicode(ch, true));
            }
            if (batch.Count >= 64)
            {
                Send(batch.ToArray());
                batch.Clear();
                await Task.Delay(5); // let the target app keep up
            }
        }
        if (batch.Count > 0) Send(batch.ToArray());
    }

    // ---- Mouse -------------------------------------------------------------------------------

    private const uint INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020, MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800, MOUSEEVENTF_HWHEEL = 0x1000;
    private const int WHEEL_DELTA = 120;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out POINT p);

    private static INPUT Mouse(uint flags, int data = 0) => new()
    {
        type = INPUT_MOUSE,
        U = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags, mouseData = unchecked((uint)data) } },
    };

    /// <summary>Cursor position in physical screen pixels (virtual desktop: may be negative on other monitors).</summary>
    public static (int X, int Y) CursorPosition()
    {
        if (!GetCursorPos(out var p)) throw new ActionException(L.T("Impossibile leggere la posizione del mouse"));
        return (p.X, p.Y);
    }

    public static void MoveCursor(int x, int y)
    {
        if (!SetCursorPos(x, y)) throw new ActionException(L.T("Impossibile spostare il mouse (PC bloccato?)"));
    }

    private static (uint Down, uint Up) ButtonFlags(string button) => button switch
    {
        "right" => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
        "middle" => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
        _ => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP),
    };

    public static void MouseButton(string button, bool down)
    {
        var (d, u) = ButtonFlags(button);
        Send([Mouse(down ? d : u)]);
    }

    public static async Task MouseClickAsync(string button, int count)
    {
        var (d, u) = ButtonFlags(button);
        for (var i = 0; i < count; i++)
        {
            if (i > 0) await Task.Delay(40); // inside the double-click time
            Send([Mouse(d)]);
            await Task.Delay(15);
            Send([Mouse(u)]);
        }
    }

    /// <summary>Scrolls by notches: positive = up (or right when horizontal).</summary>
    public static void MouseWheel(int notches, bool horizontal) =>
        Send([Mouse(horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL, notches * WHEEL_DELTA)]);

    public static void TapVirtualKey(ushort vk) => Send([Make(Vk(vk), false), Make(Vk(vk), true)]);

    /// <summary>
    /// Lets the next launched process take the foreground. Windows only allows this for the process
    /// that received the last input, so a harmless key event is injected first.
    /// </summary>
    public static void AllowForegroundForLaunchedApps()
    {
        try
        {
            // VK 0xFF is unassigned: it is ignored by applications but counts as input for this process.
            var none = new Stroke(0xFF, 0, false, false);
            SendInput(2, [Make(none, false), Make(none, true)], Marshal.SizeOf<INPUT>());
            AllowSetForegroundWindow(-1);
        }
        catch { /* best effort */ }
    }
}
