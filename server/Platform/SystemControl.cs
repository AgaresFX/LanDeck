using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using PadNook.Actions;
using PadNook.Core;

namespace PadNook.Platform;

/// <summary>Power, screenshots and brightness.</summary>
internal static class SystemControl
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool LockWorkStation();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ExitWindowsEx(uint flags, uint reason);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    public static void Lock()
    {
        if (!LockWorkStation()) throw new ActionException(L.T("Impossibile bloccare il PC"));
    }

    public static void Sleep(bool hibernate)
    {
        // Give the WebSocket a moment to deliver the (empty) result before the PC goes down.
        Task.Delay(300).ContinueWith(_ =>
        {
            if (!SetSuspendState(hibernate, false, false))
                Log.Error($"SetSuspendState failed: {Marshal.GetLastWin32Error()}");
        });
    }

    public static void LogOff()
    {
        Task.Delay(300).ContinueWith(_ => ExitWindowsEx(0 /* EWX_LOGOFF */, 0));
    }

    /// <param name="args">shutdown.exe arguments, e.g. "/s /t 0".</param>
    public static void Shutdown(string args)
    {
        var psi = new ProcessStartInfo("shutdown.exe", args) { UseShellExecute = false, CreateNoWindow = true };
        try { Process.Start(psi)?.Dispose(); }
        catch (Win32Exception ex) { throw new ActionException(L.F("Impossibile eseguire shutdown: {0}", ex.Message)); }
    }

    // ---- Screenshot ---------------------------------------------------------------------------

    public static string DefaultScreenshotFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "PadNook");

    /// <param name="area">all = every monitor, primary, window = active window.</param>
    /// <param name="target">file, clipboard or both.</param>
    public static string Screenshot(string area, string target, string? folder)
    {
        Rectangle rect = area switch
        {
            "primary" => Screen.PrimaryScreen?.Bounds ?? SystemInformation.VirtualScreen,
            "window" => WindowControl.ActiveWindowBounds() ?? throw new ActionException(L.T("Nessuna finestra attiva")),
            _ => SystemInformation.VirtualScreen,
        };
        if (rect.Width <= 0 || rect.Height <= 0) throw new ActionException(L.T("Area dello screenshot non valida"));

        using var bmp = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            try { g.CopyFromScreen(rect.Location, Point.Empty, rect.Size); }
            catch (Win32Exception) { throw new ActionException(L.T("Impossibile catturare lo schermo (PC bloccato?)")); }
        }

        string? saved = null;
        if (target is "file" or "both")
        {
            var dir = string.IsNullOrWhiteSpace(folder) ? DefaultScreenshotFolder : Environment.ExpandEnvironmentVariables(folder);
            Directory.CreateDirectory(dir);
            saved = Path.Combine(dir, $"Screenshot {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png");
            bmp.Save(saved, ImageFormat.Png);
        }
        if (target is "clipboard" or "both")
        {
            // The clipboard needs an STA thread.
            Exception? error = null;
            var copy = (Bitmap)bmp.Clone();
            var t = new Thread(() =>
            {
                try { Clipboard.SetImage(copy); }
                catch (Exception ex) { error = ex; }
                finally { copy.Dispose(); }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join(5000);
            if (error is not null) throw new ActionException(L.F("Impossibile copiare negli appunti: {0}", error.Message));
        }
        return saved ?? "";
    }

    // ---- Brightness ---------------------------------------------------------------------------

    private static readonly object BrightnessGate = new();

    /// <summary>delta or absolute value in percent. DDC/CI for external monitors, WMI for laptop panels.</summary>
    public static int ChangeBrightness(int? delta, int? set)
    {
        lock (BrightnessGate)
        {
            var result = -1;
            foreach (var h in Monitors.Physical())
            {
                try
                {
                    if (!Monitors.GetMonitorBrightness(h.Handle, out var min, out var cur, out var max) || max <= min) continue;
                    var pct = (cur - min) * 100.0 / (max - min);
                    var next = Math.Clamp(set ?? (int)Math.Round(pct + delta!.Value), 0, 100);
                    var raw = (uint)Math.Round(min + next * (max - min) / 100.0);
                    if (Monitors.SetMonitorBrightness(h.Handle, raw)) result = next;
                }
                finally { Monitors.DestroyPhysicalMonitor(h.Handle); }
            }
            if (result >= 0) return result;
            return WmiBrightness(delta, set);
        }
    }

    /// <summary>Brightness in percent of the first monitor that reports it via DDC/CI, or null.</summary>
    public static int? GetBrightness()
    {
        lock (BrightnessGate)
        {
            int? result = null;
            foreach (var h in Monitors.Physical())
            {
                try
                {
                    if (result is null && Monitors.GetMonitorBrightness(h.Handle, out var min, out var cur, out var max) && max > min)
                        result = (int)Math.Round((cur - min) * 100.0 / (max - min));
                }
                finally { Monitors.DestroyPhysicalMonitor(h.Handle); }
            }
            return result;
        }
    }

    /// <summary>Brightness of a laptop panel via WMI (slow: starts PowerShell), or null.</summary>
    public static int? GetBrightnessWmi()
    {
        var script = "(Get-CimInstance -Namespace root/WMI -ClassName WmiMonitorBrightness -ErrorAction Stop | Select-Object -First 1).CurrentBrightness";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -EncodedCommand {encoded}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        try
        {
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);
            return p.ExitCode == 0 && int.TryParse(output.Trim(), out var n) ? n : null;
        }
        catch { return null; }
    }

    private static int WmiBrightness(int? delta, int? set)
    {
        var expr = set is not null ? $"{set.Value}" : $"$c + ({delta!.Value})";
        var script =
            "$ErrorActionPreference='Stop';" +
            "$b = Get-CimInstance -Namespace root/WMI -ClassName WmiMonitorBrightness | Select-Object -First 1;" +
            "$c = [int]$b.CurrentBrightness;" +
            $"$n = [Math]::Max(0, [Math]::Min(100, {expr}));" +
            "$m = Get-CimInstance -Namespace root/WMI -ClassName WmiMonitorBrightnessMethods | Select-Object -First 1;" +
            "Invoke-CimMethod -InputObject $m -MethodName WmiSetBrightness -Arguments @{Timeout=0; Brightness=[byte]$n} | Out-Null;" +
            "Write-Output $n";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -EncodedCommand {encoded}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(10000);
        if (p.ExitCode == 0 && int.TryParse(output.Trim(), out var n)) return n;
        throw new ActionException(L.T("Nessuno schermo permette di regolare la luminosità. Sui monitor esterni attiva «DDC/CI» nel menu del monitor."));
    }

    private static class Monitors
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct PHYSICAL_MONITOR
        {
            public IntPtr Handle;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        }

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);

        [DllImport("dxva2.dll", SetLastError = true)]
        public static extern bool DestroyPhysicalMonitor(IntPtr hMonitor);

        [DllImport("dxva2.dll", SetLastError = true)]
        public static extern bool GetMonitorBrightness(IntPtr hMonitor, out uint min, out uint current, out uint max);

        [DllImport("dxva2.dll", SetLastError = true)]
        public static extern bool SetMonitorBrightness(IntPtr hMonitor, uint brightness);

        public static List<PHYSICAL_MONITOR> Physical()
        {
            var list = new List<PHYSICAL_MONITOR>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMon, _, _, _) =>
            {
                if (GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out var n) && n > 0)
                {
                    var arr = new PHYSICAL_MONITOR[n];
                    if (GetPhysicalMonitorsFromHMONITOR(hMon, n, arr)) list.AddRange(arr);
                }
                return true;
            }, IntPtr.Zero);
            return list;
        }
    }
}

/// <summary>CPU and RAM usage.</summary>
internal static class SystemStats
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME { public uint Low, High; public readonly ulong Value => ((ulong)High << 32) | Low; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);

    private static ulong _lastIdle, _lastTotal;

    /// <summary>CPU usage (%) since the previous call.</summary>
    public static double Cpu()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        var total = kernel.Value + user.Value; // kernel time includes idle time
        var dIdle = idle.Value - _lastIdle;
        var dTotal = total - _lastTotal;
        _lastIdle = idle.Value;
        _lastTotal = total;
        return dTotal == 0 ? 0 : Math.Clamp(100.0 * (dTotal - dIdle) / dTotal, 0, 100);
    }

    public static (double Percent, double UsedGb, double TotalGb) Ram()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref m)) return (0, 0, 0);
        var total = m.ullTotalPhys / 1073741824.0;
        var used = (m.ullTotalPhys - m.ullAvailPhys) / 1073741824.0;
        return (m.dwMemoryLoad, used, total);
    }
}
