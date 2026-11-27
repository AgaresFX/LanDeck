using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using PadNook.Actions;

namespace PadNook.Platform;

public sealed record ForegroundApp(string Exe, string Title);

/// <summary>Window management (maximize, snap, move to monitor, bring to front…) and foreground app detection.</summary>
internal static class WindowControl
{
    private const int SW_MAXIMIZE = 3, SW_MINIMIZE = 6, SW_RESTORE = 9;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80;
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
    private const uint WM_CLOSE = 0x10;
    private const uint GW_OWNER = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT rect, int size);

    // ---- Foreground app -----------------------------------------------------------------------

    /// <summary>The foreground window handle: a cheap call, fine to poll often.</summary>
    public static IntPtr ForegroundHandle() => GetForegroundWindow();

    /// <summary>Title of a window (cheap).</summary>
    public static string WindowTitle(IntPtr hwnd) => TitleOf(hwnd);

    public static ForegroundApp? GetForeground() => GetForeground(GetForegroundWindow());

    public static ForegroundApp? GetForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        var pid = PidOf(hwnd);
        var name = ProcessName(pid);
        // UWP apps live inside ApplicationFrameHost: the real process owns a child window.
        if (name == "applicationframehost")
        {
            EnumChildWindows(hwnd, (child, _) =>
            {
                var cp = PidOf(child);
                if (cp != pid) { name = ProcessName(cp); return false; }
                return true;
            }, IntPtr.Zero);
        }
        return name.Length == 0 ? null : new ForegroundApp(name, TitleOf(hwnd));
    }

    /// <summary>Processes (lowercase name without .exe) that have a visible top-level window.</summary>
    public static List<string> WindowedApps()
    {
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        EnumWindows((h, _) =>
        {
            if (IsAppWindow(h)) { var n = ProcessName(PidOf(h)); if (n.Length > 0) set.Add(n); }
            return true;
        }, IntPtr.Zero);
        set.Remove("applicationframehost");
        set.Remove("textinputhost");
        return set.ToList();
    }

    private static uint PidOf(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid;
    }

    private static string ProcessName(uint pid)
    {
        if (pid == 0) return "";
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName.ToLowerInvariant();
        }
        catch { return ""; }
    }

    private static string TitleOf(IntPtr hwnd)
    {
        var len = GetWindowTextLength(hwnd);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static bool IsAppWindow(IntPtr h) =>
        IsWindowVisible(h)
        && GetWindow(h, GW_OWNER) == IntPtr.Zero
        && (GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) == 0
        && GetWindowTextLength(h) > 0;

    // ---- Targets ------------------------------------------------------------------------------

    /// <summary>The window of an app (topmost in z-order) or, with no app, the foreground window.</summary>
    private static IntPtr Find(string? app)
    {
        if (string.IsNullOrWhiteSpace(app))
        {
            var fg = GetForegroundWindow();
            return fg;
        }
        var target = CoreAudio.NormalizeApp(app);
        var found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!IsAppWindow(h) || ProcessName(PidOf(h)) != target) return true;
            found = h;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>All app windows of a process (topmost first in z-order).</summary>
    private static List<IntPtr> FindAll(string app)
    {
        var target = CoreAudio.NormalizeApp(app);
        var list = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            if (IsAppWindow(h) && ProcessName(PidOf(h)) == target) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>What a launch key does when its app already has a window open. Returns true if it handled it.</summary>
    public static bool HandleAlreadyOpen(string app, string mode)
    {
        var windows = FindAll(app);
        if (windows.Count == 0) return false;
        switch (mode)
        {
            case "focus":
                Focus(windows[0]);
                return true;
            case "close":
                // Same as clicking the X: apps can still ask to save.
                foreach (var w in windows) PostMessage(w, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                return true;
            default:
                return false;
        }
    }

    public static Rectangle? ActiveWindowBounds()
    {
        var h = GetForegroundWindow();
        return h == IntPtr.Zero ? null : VisibleBounds(h);
    }

    /// <summary>Window bounds without the invisible resize borders of Windows 10/11.</summary>
    private static Rectangle VisibleBounds(IntPtr h)
    {
        if (DwmGetWindowAttribute(h, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out var r, Marshal.SizeOf<RECT>()) != 0)
            GetWindowRect(h, out r);
        return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }

    // ---- Commands -----------------------------------------------------------------------------

    public static void Execute(string command, string? app, string? launch, string? region)
    {
        var h = Find(app);
        if (h == IntPtr.Zero)
        {
            if (command == "focus" && !string.IsNullOrWhiteSpace(launch))
            {
                Launch(launch);
                return;
            }
            throw new ActionException(string.IsNullOrWhiteSpace(app)
                ? L.T("Nessuna finestra attiva")
                : L.F("Nessuna finestra aperta di «{0}»", app));
        }

        switch (command)
        {
            case "maximize": ShowWindow(h, SW_MAXIMIZE); break;
            case "minimize": ShowWindow(h, SW_MINIMIZE); break;
            case "restore": ShowWindow(h, SW_RESTORE); break;
            case "toggleMax": ShowWindow(h, IsZoomed(h) ? SW_RESTORE : SW_MAXIMIZE); break;
            case "close": PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); break;
            case "focus": Focus(h); break;
            case "topmost":
            {
                var on = (GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;
                SetWindowPos(h, new IntPtr(on ? -2 : -1), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                break;
            }
            case "nextMonitor": MoveToNextMonitor(h); break;
            case "snap": Snap(h, region ?? "left"); break;
            default: throw new ActionException(L.T("Comando finestra non valido"));
        }
    }

    private static void Launch(string target)
    {
        var path = Environment.ExpandEnvironmentVariables(target.Trim().Trim('"'));
        try
        {
            Win32Input.AllowForegroundForLaunchedApps();
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Win32Exception ex) { throw new ActionException(L.F("Impossibile aprire \"{0}\": {1}", path, ex.Message)); }
    }

    /// <summary>Brings a window to the front, working around the foreground lock.</summary>
    public static void Focus(IntPtr h)
    {
        if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
        Win32Input.AllowForegroundForLaunchedApps(); // makes this process the last input source
        if (SetForegroundWindow(h)) return;

        var fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var me = GetCurrentThreadId();
        AttachThreadInput(me, fgThread, true);
        try
        {
            BringWindowToTop(h);
            SetForegroundWindow(h);
        }
        finally { AttachThreadInput(me, fgThread, false); }
    }

    private static void MoveToNextMonitor(IntPtr h)
    {
        var screens = Screen.AllScreens.OrderBy(s => s.Bounds.Left).ThenBy(s => s.Bounds.Top).ToList();
        if (screens.Count < 2) throw new ActionException(L.T("C'è un solo schermo"));
        var bounds = VisibleBounds(h);
        var current = Screen.FromRectangle(bounds);
        var idx = screens.FindIndex(s => s.DeviceName == current.DeviceName);
        var next = screens[(idx + 1) % screens.Count];

        var wasMax = IsZoomed(h);
        if (wasMax) ShowWindow(h, SW_RESTORE);
        bounds = VisibleBounds(h);
        var from = current.WorkingArea;
        var to = next.WorkingArea;
        // Same relative position and size on the new monitor.
        var w = Math.Min(to.Width, (int)Math.Round(bounds.Width * (double)to.Width / from.Width));
        var hgt = Math.Min(to.Height, (int)Math.Round(bounds.Height * (double)to.Height / from.Height));
        var x = to.Left + (int)Math.Round((bounds.Left - from.Left) * (double)to.Width / from.Width);
        var y = to.Top + (int)Math.Round((bounds.Top - from.Top) * (double)to.Height / from.Height);
        x = Math.Clamp(x, to.Left, to.Right - w);
        y = Math.Clamp(y, to.Top, to.Bottom - hgt);
        Place(h, new Rectangle(x, y, w, hgt));
        if (wasMax) ShowWindow(h, SW_MAXIMIZE);
    }

    private static void Snap(IntPtr h, string region)
    {
        if (IsZoomed(h) || IsIconic(h)) ShowWindow(h, SW_RESTORE);
        var wa = Screen.FromRectangle(VisibleBounds(h)).WorkingArea;
        int hw = wa.Width / 2, hh = wa.Height / 2;
        var r = region switch
        {
            "left" => new Rectangle(wa.Left, wa.Top, hw, wa.Height),
            "right" => new Rectangle(wa.Left + hw, wa.Top, wa.Width - hw, wa.Height),
            "top" => new Rectangle(wa.Left, wa.Top, wa.Width, hh),
            "bottom" => new Rectangle(wa.Left, wa.Top + hh, wa.Width, wa.Height - hh),
            "topLeft" => new Rectangle(wa.Left, wa.Top, hw, hh),
            "topRight" => new Rectangle(wa.Left + hw, wa.Top, wa.Width - hw, hh),
            "bottomLeft" => new Rectangle(wa.Left, wa.Top + hh, hw, wa.Height - hh),
            "bottomRight" => new Rectangle(wa.Left + hw, wa.Top + hh, wa.Width - hw, wa.Height - hh),
            "leftThird" => new Rectangle(wa.Left, wa.Top, wa.Width / 3, wa.Height),
            "centerThird" => new Rectangle(wa.Left + wa.Width / 3, wa.Top, wa.Width / 3, wa.Height),
            "rightThird" => new Rectangle(wa.Left + 2 * wa.Width / 3, wa.Top, wa.Width - 2 * wa.Width / 3, wa.Height),
            "center" => CenterOf(VisibleBounds(h), wa),
            _ => throw new ActionException(L.T("Posizione non valida")),
        };
        Place(h, r);
    }

    private static Rectangle CenterOf(Rectangle b, Rectangle wa)
    {
        var w = Math.Min(b.Width, wa.Width);
        var hgt = Math.Min(b.Height, wa.Height);
        return new Rectangle(wa.Left + (wa.Width - w) / 2, wa.Top + (wa.Height - hgt) / 2, w, hgt);
    }

    /// <summary>Moves the visible frame of the window exactly onto r (compensating the invisible borders).</summary>
    private static void Place(IntPtr h, Rectangle r)
    {
        GetWindowRect(h, out var outer);
        var vis = VisibleBounds(h);
        int dl = vis.Left - outer.Left, dt = vis.Top - outer.Top, dr = outer.Right - vis.Right, db = outer.Bottom - vis.Bottom;
        SetWindowPos(h, IntPtr.Zero, r.Left - dl, r.Top - dt, r.Width + dl + dr, r.Height + dt + db, SWP_NOZORDER | SWP_NOACTIVATE);
    }
}
