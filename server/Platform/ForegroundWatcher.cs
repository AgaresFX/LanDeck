using System.Runtime.InteropServices;

namespace PadNook.Platform;

/// <summary>
/// Signals when Windows changes the foreground window (SetWinEventHook on EVENT_SYSTEM_FOREGROUND),
/// so the foreground app is detected immediately instead of waiting for the next poll.
/// The hook runs on its own thread with a message loop, as out-of-context WinEvent hooks require.
/// </summary>
internal sealed class ForegroundWatcher : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000, WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const uint WM_QUIT = 0x0012;

    private delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint tid, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly WinEventProc _proc; // kept alive: the native hook calls into it
    private uint _threadId;

    /// <summary>True once the hook is installed; if false, callers rely on polling only.</summary>
    public bool Active { get; private set; }

    public ForegroundWatcher()
    {
        _proc = (_, _, _, _, _, _, _) => Signal();
        using var ready = new ManualResetEventSlim();
        var t = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            const uint flags = WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS;
            var h1 = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _proc, 0, 0, flags);
            var h2 = SetWinEventHook(EVENT_SYSTEM_MINIMIZEEND, EVENT_SYSTEM_MINIMIZEEND, IntPtr.Zero, _proc, 0, 0, flags);
            Active = h1 != IntPtr.Zero;
            ready.Set();
            if (Active)
            {
                while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
            }
            if (h1 != IntPtr.Zero) UnhookWinEvent(h1);
            if (h2 != IntPtr.Zero) UnhookWinEvent(h2);
        })
        { IsBackground = true, Name = "ForegroundWatcher" };
        t.Start();
        ready.Wait(TimeSpan.FromSeconds(2));
    }

    private void Signal()
    {
        try { _signal.Release(); } catch (SemaphoreFullException) { }
    }

    /// <summary>Waits for a foreground change or the timeout. True if Windows signaled a change.</summary>
    public Task<bool> WaitAsync(int timeoutMs, CancellationToken ct) => _signal.WaitAsync(timeoutMs, ct);

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }
}
