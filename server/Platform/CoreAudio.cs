using System.Diagnostics;
using System.Runtime.InteropServices;
using PadNook.Actions;

namespace PadNook.Platform;

public sealed record AudioDevice(string Id, string Name, bool IsDefault);

/// <summary>
/// Windows Core Audio: master volume/mute, microphone mute, per-app volume (audio sessions)
/// and the default output device.
/// </summary>
internal static class CoreAudio
{
    private const int eRender = 0, eCapture = 1;
    private const int eConsole = 0, eMultimedia = 1, eCommunications = 2;
    private const int DEVICE_STATE_ACTIVE = 1;
    private const int CLSCTX_ALL = 23;

    private static readonly object Gate = new();

    // ---- Master volume ------------------------------------------------------------------------

    public static float StepVolume(float delta) => WithEndpoint(eRender, vol =>
    {
        Check(vol.GetMasterVolumeLevelScalar(out var current));
        var next = Math.Clamp(current + delta, 0f, 1f);
        var ctx = Guid.Empty;
        Check(vol.SetMasterVolumeLevelScalar(next, ref ctx));
        if (delta > 0 && vol.GetMute(out var muted) == 0 && muted) Check(vol.SetMute(false, ref ctx));
        return next;
    });

    public static void SetVolume(float level) => WithEndpoint(eRender, vol =>
    {
        var ctx = Guid.Empty;
        Check(vol.SetMasterVolumeLevelScalar(Math.Clamp(level, 0f, 1f), ref ctx));
        if (level > 0 && vol.GetMute(out var muted) == 0 && muted) Check(vol.SetMute(false, ref ctx));
        return 0;
    });

    public static bool ToggleMute() => SetMute(eRender, null);

    /// <summary>Volume (0..1) and mute of the default output, or null if there is none.</summary>
    public static (float Volume, bool Muted)? GetMasterState() => TryGetState(eRender);

    // ---- Microphone ---------------------------------------------------------------------------

    /// <summary>mute: null = toggle. Returns the new state (true = muted).</summary>
    public static bool SetMicMute(bool? mute) => SetMute(eCapture, mute);

    /// <summary>true = muted, null = no microphone.</summary>
    public static bool? GetMicMuted() => TryGetState(eCapture)?.Muted;

    /// <summary>Volume (0..1) and mute of the default microphone, or null if there is none.</summary>
    public static (float Volume, bool Muted)? GetMicState() => TryGetState(eCapture);

    public static void SetMicVolume(float level) => WithEndpoint(eCapture, vol =>
    {
        var ctx = Guid.Empty;
        Check(vol.SetMasterVolumeLevelScalar(Math.Clamp(level, 0f, 1f), ref ctx));
        return 0;
    });

    private static bool SetMute(int flow, bool? mute) => WithEndpoint(flow, vol =>
    {
        Check(vol.GetMute(out var muted));
        var next = mute ?? !muted;
        var ctx = Guid.Empty;
        Check(vol.SetMute(next, ref ctx));
        return next;
    });

    private static (float Volume, bool Muted)? TryGetState(int flow)
    {
        try
        {
            return WithEndpoint(flow, vol =>
            {
                Check(vol.GetMasterVolumeLevelScalar(out var v));
                Check(vol.GetMute(out var m));
                return ((float Volume, bool Muted)?)(v, m);
            });
        }
        catch { return null; }
    }

    // ---- A specific device (slider keys) --------------------------------------------------------

    /// <summary>Volume (0..1) and mute of a device by id, or null if it is not available.</summary>
    public static (float Volume, bool Muted)? GetDeviceState(string id)
    {
        try
        {
            return WithEndpoint(eRender, vol =>
            {
                Check(vol.GetMasterVolumeLevelScalar(out var v));
                Check(vol.GetMute(out var m));
                return ((float Volume, bool Muted)?)(v, m);
            }, id);
        }
        catch { return null; }
    }

    public static void SetDeviceVolume(string id, float level) => WithEndpoint(eRender, vol =>
    {
        var ctx = Guid.Empty;
        Check(vol.SetMasterVolumeLevelScalar(Math.Clamp(level, 0f, 1f), ref ctx));
        return 0;
    }, id);

    public static bool ToggleDeviceMute(string id) => WithEndpoint(eRender, vol =>
    {
        Check(vol.GetMute(out var muted));
        var ctx = Guid.Empty;
        Check(vol.SetMute(!muted, ref ctx));
        return !muted;
    }, id);

    private static T WithEndpoint<T>(int flow, Func<IAudioEndpointVolume, T> fn, string? deviceId = null)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        try
        {
            int hr;
            IMMDevice device;
            if (deviceId is not null)
            {
                hr = enumerator.GetDevice(deviceId, out device);
                if (hr != 0) throw new ActionException(L.T("Dispositivo audio non trovato (scollegato?)"));
            }
            else
            {
                hr = enumerator.GetDefaultAudioEndpoint(flow, flow == eCapture ? eCommunications : eMultimedia, out device);
                if (hr != 0 && flow == eCapture) hr = enumerator.GetDefaultAudioEndpoint(flow, eConsole, out device);
            }
            if (hr != 0)
                throw new ActionException(flow == eCapture ? L.T("Nessun microfono trovato") : L.T("Nessun dispositivo audio di uscita trovato"));
            try
            {
                var iid = typeof(IAudioEndpointVolume).GUID;
                Check(device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var obj));
                var vol = (IAudioEndpointVolume)obj;
                try { return fn(vol); }
                finally { Marshal.ReleaseComObject(vol); }
            }
            finally { Marshal.ReleaseComObject(device); }
        }
        finally { Marshal.ReleaseComObject(enumerator); }
    }

    // ---- Output devices -----------------------------------------------------------------------

    public static List<AudioDevice> ListOutputs() => ListDevices(eRender);

    public static List<AudioDevice> ListInputs() => ListDevices(eCapture);

    private static List<AudioDevice> ListDevices(int flow)
    {
        var list = new List<AudioDevice>();
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        try
        {
            string? defaultId = null;
            if (enumerator.GetDefaultAudioEndpoint(flow, flow == eCapture ? eCommunications : eMultimedia, out var def) == 0)
            {
                def.GetId(out defaultId);
                Marshal.ReleaseComObject(def);
            }
            Check(enumerator.EnumAudioEndpoints(flow, DEVICE_STATE_ACTIVE, out var coll));
            try
            {
                Check(coll.GetCount(out var n));
                for (uint i = 0; i < n; i++)
                {
                    if (coll.Item(i, out var dev) != 0) continue;
                    try
                    {
                        dev.GetId(out var id);
                        list.Add(new AudioDevice(id, FriendlyName(dev) ?? id, id == defaultId));
                    }
                    finally { Marshal.ReleaseComObject(dev); }
                }
            }
            finally { Marshal.ReleaseComObject(coll); }
        }
        finally { Marshal.ReleaseComObject(enumerator); }
        return list;
    }

    public static AudioDevice? DefaultOutput() => ListOutputs().FirstOrDefault(d => d.IsDefault);

    /// <summary>Makes a device the default output for all roles (console, multimedia, communications).</summary>
    public static void SetDefaultOutput(string id)
    {
        var policy = (IPolicyConfig)new PolicyConfigClient();
        try
        {
            foreach (var role in new[] { eConsole, eMultimedia, eCommunications })
                Check(policy.SetDefaultEndpoint(id, role));
        }
        finally { Marshal.ReleaseComObject(policy); }
    }

    private static string? FriendlyName(IMMDevice dev)
    {
        if (dev.OpenPropertyStore(0 /* STGM_READ */, out var store) != 0) return null;
        try
        {
            var key = new PROPERTYKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
            if (store.GetValue(ref key, out var pv) != 0) return null;
            try { return pv.vt == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(pv.p) : null; }
            finally { PropVariantClear(ref pv); }
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    // ---- Per-app volume (audio sessions) -----------------------------------------------------

    private static readonly Dictionary<uint, (string Name, DateTime Seen)> ProcessNames = new();

    /// <summary>"Spotify.exe", "spotify" -> "spotify".</summary>
    public static string NormalizeApp(string app)
    {
        var s = app.Trim().Trim('"');
        s = Path.GetFileName(s);
        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
        return s.ToLowerInvariant();
    }

    private static string ProcessName(uint pid)
    {
        lock (ProcessNames)
        {
            if (ProcessNames.TryGetValue(pid, out var e) && DateTime.UtcNow - e.Seen < TimeSpan.FromMinutes(1)) return e.Name;
            string name;
            try
            {
                using var p = Process.GetProcessById((int)pid);
                name = p.ProcessName.ToLowerInvariant();
            }
            catch { name = ""; }
            ProcessNames[pid] = (name, DateTime.UtcNow);
            return name;
        }
    }

    /// <summary>Runs fn on every audio session of every active output device.</summary>
    private static void ForEachSession(Action<string, ISimpleAudioVolume> fn)
    {
        lock (Gate)
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            try
            {
                Check(enumerator.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, out var coll));
                try
                {
                    Check(coll.GetCount(out var n));
                    for (uint i = 0; i < n; i++)
                    {
                        if (coll.Item(i, out var dev) != 0) continue;
                        try { ForEachSessionOf(dev, fn); }
                        finally { Marshal.ReleaseComObject(dev); }
                    }
                }
                finally { Marshal.ReleaseComObject(coll); }
            }
            finally { Marshal.ReleaseComObject(enumerator); }
        }
    }

    private static void ForEachSessionOf(IMMDevice dev, Action<string, ISimpleAudioVolume> fn)
    {
        var iid = typeof(IAudioSessionManager2).GUID;
        if (dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var obj) != 0) return;
        var mgr = (IAudioSessionManager2)obj;
        try
        {
            if (mgr.GetSessionEnumerator(out var sessions) != 0) return;
            try
            {
                sessions.GetCount(out var count);
                for (var i = 0; i < count; i++)
                {
                    if (sessions.GetSession(i, out var ctlObj) != 0) continue;
                    try
                    {
                        if (ctlObj is not IAudioSessionControl2 ctl) continue;
                        if (ctl.GetProcessId(out var pid) != 0 || pid == 0) continue;
                        var name = ProcessName(pid);
                        if (name.Length == 0) continue;
                        if (ctlObj is ISimpleAudioVolume vol) fn(name, vol);
                    }
                    finally { Marshal.ReleaseComObject(ctlObj); }
                }
            }
            finally { Marshal.ReleaseComObject(sessions); }
        }
        finally { Marshal.ReleaseComObject(mgr); }
    }

    /// <summary>Apps that currently have an audio session.</summary>
    public static List<string> ListSessionApps()
    {
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        try { ForEachSession((name, _) => set.Add(name)); } catch { }
        return set.ToList();
    }

    /// <summary>delta = volume change; mute: null = no change, true/false = set, toggle = flip.</summary>
    public static (float Volume, bool Muted) ChangeApp(string app, float? delta = null, float? set = null, bool toggleMute = false)
    {
        var target = NormalizeApp(app);
        if (target.Length == 0) throw new ActionException(L.T("Nessuna app configurata"));
        var found = false;
        float vOut = 0;
        bool mOut = false;
        bool? muteTo = null;
        ForEachSession((name, vol) =>
        {
            if (name != target) return;
            var ctx = Guid.Empty;
            vol.GetMasterVolume(out var v);
            vol.GetMute(out var m);
            if (toggleMute)
            {
                muteTo ??= !m; // all sessions of the app follow the first one
                vol.SetMute(muteTo.Value, ref ctx);
                m = muteTo.Value;
            }
            if (delta is not null || set is not null)
            {
                v = Math.Clamp(set ?? v + delta!.Value, 0f, 1f);
                vol.SetMasterVolume(v, ref ctx);
                if (m && (delta > 0 || set > 0)) { vol.SetMute(false, ref ctx); m = false; }
            }
            found = true;
            vOut = v;
            mOut = m;
        });
        if (!found) throw new ActionException(L.F("«{0}» non sta riproducendo audio (apri l'app e avvia un suono)", app));
        return (vOut, mOut);
    }

    /// <summary>Volume/mute of the given apps (normalized names) that have a session.</summary>
    public static Dictionary<string, (float Volume, bool Muted)> GetAppStates(IReadOnlySet<string> apps)
    {
        var res = new Dictionary<string, (float, bool)>();
        if (apps.Count == 0) return res;
        try
        {
            ForEachSession((name, vol) =>
            {
                if (!apps.Contains(name) || res.ContainsKey(name)) return;
                vol.GetMasterVolume(out var v);
                vol.GetMute(out var m);
                res[name] = (v, m);
            });
        }
        catch { }
        return res;
    }

    private static void Check(int hr)
    {
        if (hr != 0) throw new ActionException(L.F("Errore Core Audio (0x{0:X8})", hr));
    }

    // ---- COM interop ---------------------------------------------------------------------------

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT pvar);

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public int pid;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr p;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigClient { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, int flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, int flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        // IAudioSessionControl
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName(IntPtr name, ref Guid ctx);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath(IntPtr path, ref Guid ctx);
        [PreserveSig] int GetGroupingParam(out Guid param);
        [PreserveSig] int SetGroupingParam(ref Guid param, ref Guid ctx);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
        // IAudioSessionControl2
        [PreserveSig] int GetSessionIdentifier(out IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
        [PreserveSig] int GetProcessId(out uint pid);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    /// <summary>Undocumented but stable since Windows 7: the only way to change the default device.</summary>
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat();
        [PreserveSig] int GetDeviceFormat();
        [PreserveSig] int ResetDeviceFormat();
        [PreserveSig] int SetDeviceFormat();
        [PreserveSig] int GetProcessingPeriod();
        [PreserveSig] int SetProcessingPeriod();
        [PreserveSig] int GetShareMode();
        [PreserveSig] int SetShareMode();
        [PreserveSig] int GetPropertyValue();
        [PreserveSig] int SetPropertyValue();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
    }
}
