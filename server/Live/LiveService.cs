using System.Text.Json.Nodes;
using PadNook.Config;
using PadNook.Core;
using PadNook.Platform;

namespace PadNook.Live;

/// <summary>
/// Real-time values shown by dynamic keys ("live sources"). Each value is a small JSON object:
/// { text, value?, state?, sub?, icon? }. Source ids: cpu, ram, vol, mic, audio.out, app.fg,
/// appvol:NAME, weather:LAT,LON, counter:NAME, timer:NAME, obs.*, plugin:PLUGIN/SOURCE.
/// </summary>
public sealed class LiveService : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (JsonObject Value, string Json)> _values = new();
    private readonly Func<bool> _hasClients;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<ForegroundApp> _recentApps = new();
    private HashSet<string> _referenced = new();
    private ForegroundApp? _fg;

    public LiveStateStore State { get; }
    public WeatherService Weather { get; }

    /// <summary>Changed values (key -> value), batched.</summary>
    public event Action<JsonObject>? Changed;

    /// <summary>The foreground app on the PC changed.</summary>
    public event Action<ForegroundApp>? ForegroundChanged;

    /// <summary>The set of sources used by the configuration changed.</summary>
    public event Action<IReadOnlySet<string>>? ReferencedChanged;

    public LiveService(AppPaths paths, ConfigStore config, Func<bool> hasClients)
    {
        _hasClients = hasClients;
        State = new LiveStateStore(paths, this);
        Weather = new WeatherService(this);
        config.ProfilesChanged += () => Rescan(config);
        Rescan(config);
    }

    public void Start()
    {
        _ = Task.Run(() => SlowLoopAsync(_cts.Token));
        _ = Task.Run(() => ForegroundLoopAsync(_cts.Token));
        State.PublishAll();
    }

    public ForegroundApp? Foreground { get { lock (_gate) return _fg; } }

    /// <summary>True while at least one device is connected.</summary>
    public bool HasClients => _hasClients();

    public IReadOnlySet<string> Referenced { get { lock (_gate) return _referenced; } }

    public List<ForegroundApp> RecentApps()
    {
        lock (_gate) return _recentApps.ToList();
    }

    // ---- Values -------------------------------------------------------------------------------

    public JsonObject Snapshot()
    {
        lock (_gate)
        {
            var o = new JsonObject();
            foreach (var (k, v) in _values) o[k] = v.Value.DeepClone();
            return o;
        }
    }

    public void Publish(string key, JsonObject value) => PublishMany([(key, value)]);

    public void PublishMany(IEnumerable<(string Key, JsonObject Value)> items)
    {
        var changed = new JsonObject();
        lock (_gate)
        {
            foreach (var (key, value) in items)
            {
                var json = value.ToJsonString();
                if (_values.TryGetValue(key, out var old) && old.Json == json) continue;
                _values[key] = (value, json);
                changed[key] = value.DeepClone();
            }
        }
        if (changed.Count > 0) Changed?.Invoke(changed);
    }

    public void Remove(string key)
    {
        bool removed;
        lock (_gate) removed = _values.Remove(key);
        if (removed) Changed?.Invoke(new JsonObject { [key] = null });
    }

    public static JsonObject V(string text, double? value = null, bool? state = null, string? sub = null, string? icon = null)
    {
        var o = new JsonObject { ["text"] = text };
        if (value is not null) o["value"] = Math.Round(value.Value, 2);
        if (state is not null) o["state"] = state;
        if (sub is not null) o["sub"] = sub;
        if (icon is not null) o["icon"] = icon;
        return o;
    }

    // ---- Configuration scan -------------------------------------------------------------------

    private void Rescan(ConfigStore config)
    {
        var (_, profiles) = config.Snapshot();
        var set = new HashSet<string>(StringComparer.Ordinal);
        Walk(profiles, set);
        bool changed;
        lock (_gate)
        {
            changed = !set.SetEquals(_referenced);
            _referenced = set;
        }
        if (!changed) return;
        foreach (var src in set)
        {
            bool known;
            lock (_gate) known = _values.ContainsKey(src);
            if (!known && State?.DefaultFor(src) is { } def) Publish(src, def);
        }
        ReferencedChanged?.Invoke(set);
        Weather?.SetReferenced(set.Where(s => s.StartsWith("weather:", StringComparison.Ordinal)));
    }

    private static void Walk(JsonNode? node, HashSet<string> set)
    {
        switch (node)
        {
            case JsonObject o:
                if (o["live"] is JsonObject live && J.Str(live["source"]) is { Length: > 0 } src) set.Add(src);
                if (o["slider"] is JsonObject slider && SliderControl.SourceOf(slider) is { } ss) set.Add(ss);
                foreach (var (_, child) in o) Walk(child, set);
                break;
            case JsonArray a:
                foreach (var child in a) Walk(child, set);
                break;
        }
    }

    // ---- Polling ------------------------------------------------------------------------------

    private async Task SlowLoopAsync(CancellationToken ct)
    {
        SystemStats.Cpu(); // prime the CPU counters
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(1000, ct); }
            catch (OperationCanceledException) { return; }
            // Timers end (and alert on the PC) even when no device is connected, e.g. with the phone locked.
            try { State.Tick(); }
            catch (Exception ex) { Log.Error("Timer tick failed", ex); }
            if (!_hasClients()) continue;
            try { PollSystem(); }
            catch (Exception ex) { Log.Error("Live poll failed", ex); }
        }
    }

    private void PollSystem()
    {
        var items = new List<(string, JsonObject)>();
        var cpu = SystemStats.Cpu();
        items.Add(("cpu", V($"{cpu:0}%", cpu)));
        var (ramPct, used, total) = SystemStats.Ram();
        items.Add(("ram", V($"{ramPct:0}%", ramPct, sub: $"{used:0.0}/{total:0} GB")));

        items.Add(("vol", CoreAudio.GetMasterState() is { } master ? VolumeValue(master) : V("—", sub: L.T("nessuna uscita audio"))));
        var micState = CoreAudio.GetMicState();
        var mic = micState?.Muted;
        items.Add(("mic", mic is null ? V(L.T("Nessun mic"), state: false) : V(mic.Value ? L.T("Muto") : L.T("Attivo"), state: mic.Value)));

        var refs = Referenced;
        if (refs.Contains("micvol")) items.Add(("micvol", micState is { } ms ? VolumeValue(ms) : V(L.T("Nessun mic"), sub: L.T("nessun microfono"))));
        if (refs.Contains("brightness") && ++_brightnessTick % 5 == 1) PollBrightness(items);
        foreach (var r in refs.Where(r => r.StartsWith("devvol:", StringComparison.Ordinal)))
            items.Add((r, SliderControl.DeviceValue(r[7..])));
        if (refs.Contains("audio.out"))
        {
            var dev = CoreAudio.DefaultOutput();
            items.Add(("audio.out", V(dev?.Name ?? "—")));
        }

        var apps = refs.Where(r => r.StartsWith("appvol:", StringComparison.Ordinal))
            .Select(r => r[7..]).ToHashSet(StringComparer.Ordinal);
        if (apps.Count > 0)
        {
            var states = CoreAudio.GetAppStates(apps.Select(CoreAudio.NormalizeApp).ToHashSet());
            foreach (var app in apps)
            {
                items.Add(($"appvol:{app}", states.TryGetValue(CoreAudio.NormalizeApp(app), out var st) ? VolumeValue(st) : AppIdleValue));
            }
        }
        PublishMany(items);
    }

    public static JsonObject VolumeValue((float Volume, bool Muted) s)
    {
        var pct = Math.Round(s.Volume * 100);
        return V(s.Muted ? L.T("Muto") : $"{pct:0}%", pct, s.Muted);
    }

    public static JsonObject AppIdleValue => V("—", sub: L.T("non in riproduzione"));

    public static JsonObject BrightnessValue(int pct) => V($"{pct}%", pct);

    private int _brightnessTick;
    private DateTimeOffset _nextWmiBrightness;

    /// <summary>DDC/CI every few seconds; laptop panels (WMI, slow) at most once a minute.</summary>
    private void PollBrightness(List<(string, JsonObject)> items)
    {
        if (SystemControl.GetBrightness() is { } pct)
        {
            items.Add(("brightness", BrightnessValue(pct)));
            return;
        }
        if (DateTimeOffset.Now < _nextWmiBrightness) return;
        _nextWmiBrightness = DateTimeOffset.Now.AddMinutes(1);
        _ = Task.Run(() =>
        {
            var v = SystemControl.GetBrightnessWmi();
            Publish("brightness", v is { } n ? BrightnessValue(n) : V("—", sub: L.T("non regolabile")));
        });
    }

    private async Task ForegroundLoopAsync(CancellationToken ct)
    {
        // Windows signals each foreground change (WinEvent hook), so detection is immediate; a slow
        // poll stays as a fallback in case the hook isn't available or misses something. Only the
        // window handle is checked on each wake (cheap); the process name, the expensive part, is
        // resolved once per window change. A window must stay in front for SettleMs before it
        // counts, so windows passed through quickly don't switch profile.
        using var watcher = new ForegroundWatcher();
        int fallbackMs = watcher.Active ? 500 : 40;
        const int SettleMs = 60, TitleRefreshMs = 1000;
        var self = System.Diagnostics.Process.GetCurrentProcess().ProcessName.ToLowerInvariant();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        IntPtr lastHwnd = IntPtr.Zero;
        long changedAt = 0, titleAt = 0;
        bool pending = false;
        while (!ct.IsCancellationRequested)
        {
            // While a change is settling, wake exactly when it's due (or earlier on a new event).
            var wait = pending ? (int)Math.Max(1, changedAt + SettleMs - clock.ElapsedMilliseconds) : fallbackMs;
            try { await watcher.WaitAsync(wait, ct); }
            catch (OperationCanceledException) { return; }
            var now = clock.ElapsedMilliseconds;
            var hwnd = WindowControl.ForegroundHandle();
            if (hwnd != lastHwnd) { lastHwnd = hwnd; changedAt = now; pending = true; continue; }
            if (!pending)
            {
                // Same window: keep the title fresh now and then.
                if (now - titleAt >= TitleRefreshMs && hwnd != IntPtr.Zero)
                {
                    titleAt = now;
                    try
                    {
                        var t = WindowControl.WindowTitle(hwnd);
                        lock (_gate) if (_fg is { } cur && cur.Title != t) _fg = cur with { Title = t };
                    }
                    catch { }
                }
                continue;
            }
            if (now - changedAt < SettleMs) continue;
            pending = false;
            titleAt = now;

            ForegroundApp? fg;
            try { fg = WindowControl.GetForeground(hwnd); }
            catch { continue; }
            if (fg is null || fg.Exe == self) continue;

            ForegroundApp? fire = null;
            lock (_gate)
            {
                if (_fg?.Exe == fg.Exe) { _fg = fg; continue; }
                _fg = fg;
                fire = fg;
                if (fg.Exe is not ("explorer" or "searchhost" or "startmenuexperiencehost" or "shellexperiencehost" or "lockapp"))
                {
                    _recentApps.RemoveAll(a => a.Exe == fg.Exe);
                    _recentApps.Insert(0, fg);
                    if (_recentApps.Count > 15) _recentApps.RemoveAt(_recentApps.Count - 1);
                }
            }
            Publish("app.fg", V(fire.Exe, sub: fire.Title));
            ForegroundChanged?.Invoke(fire);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        State.Dispose();
    }
}
