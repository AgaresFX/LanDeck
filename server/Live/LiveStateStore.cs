using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PadNook.Core;

namespace PadNook.Live;

/// <summary>Named counters and timers (live-state.json), shared by all devices.</summary>
public sealed class LiveStateStore : IDisposable
{
    public sealed class TimerState
    {
        public string Mode { get; set; } = "countdown"; // countdown | stopwatch
        public long DurationMs { get; set; } = 5 * 60_000;
        public bool Running { get; set; }
        public long ElapsedMs { get; set; }      // accumulated while paused
        public long StartedAtMs { get; set; }    // unix ms of the last start, when running
        public bool AlertPc { get; set; } = true;    // Windows notification when a countdown ends
        public bool AlertPhone { get; set; } = true; // sound + vibration on the connected devices
    }

    /// <summary>A countdown reached zero: (name, alert on the PC, alert on the devices).</summary>
    public event Action<string, bool, bool>? TimerFinished;

    private sealed class Data
    {
        public Dictionary<string, double> Counters { get; set; } = new();
        public Dictionary<string, TimerState> Timers { get; set; } = new();
    }

    private readonly string _file;
    private readonly LiveService _live;
    private readonly object _gate = new();
    private readonly System.Threading.Timer _saveTimer;
    private Data _data = new();

    public LiveStateStore(AppPaths paths, LiveService live)
    {
        _live = live;
        _file = Path.Combine(paths.Root, "live-state.json");
        _saveTimer = new System.Threading.Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        try
        {
            if (File.Exists(_file)) _data = JsonSerializer.Deserialize<Data>(File.ReadAllText(_file)) ?? new();
        }
        catch (Exception ex) { Log.Error("live-state.json is invalid, counters reset", ex); }
    }

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // ---- Counters -----------------------------------------------------------------------------

    public double Counter(string name, string op, double step, double? set = null)
    {
        name = Clean(name);
        double v;
        lock (_gate)
        {
            _data.Counters.TryGetValue(name, out v);
            v = op switch
            {
                "inc" => v + step,
                "dec" => v - step,
                "reset" => 0,
                "set" => set ?? 0,
                _ => v,
            };
            if (v == 0) _data.Counters.Remove(name); else _data.Counters[name] = v;
            Save();
        }
        _live.Publish($"counter:{name}", CounterValue(v));
        return v;
    }

    private static JsonObject CounterValue(double v) =>
        LiveService.V(v.ToString("0.##", L.Culture), v);

    // ---- Timers -------------------------------------------------------------------------------

    /// <param name="op">toggle | start | pause | reset | config (only applies the settings)</param>
    public void Timer(string name, string op, string? mode, double? durationSec, bool? alertPc = null, bool? alertPhone = null)
    {
        name = Clean(name);
        JsonObject value;
        lock (_gate)
        {
            if (!_data.Timers.TryGetValue(name, out var t)) _data.Timers[name] = t = new TimerState();
            if (mode is "countdown" or "stopwatch") t.Mode = mode;
            if (durationSec is > 0) t.DurationMs = (long)(durationSec.Value * 1000);
            if (alertPc is not null) t.AlertPc = alertPc.Value;
            if (alertPhone is not null) t.AlertPhone = alertPhone.Value;

            var elapsed = Elapsed(t);
            var finished = t.Mode == "countdown" && elapsed >= t.DurationMs;
            switch (op)
            {
                case "toggle" when finished:
                    // Tapping a finished countdown ("FINE") only clears it; the next tap starts it.
                    t.Running = false;
                    t.ElapsedMs = 0;
                    break;
                case "start" when !t.Running:
                case "toggle" when !t.Running:
                    if (finished) t.ElapsedMs = 0; // a finished countdown starts again from the top
                    t.Running = true;
                    t.StartedAtMs = Now;
                    break;
                case "pause" when t.Running:
                case "toggle" when t.Running:
                    t.ElapsedMs = elapsed;
                    t.Running = false;
                    break;
                case "reset":
                    t.Running = false;
                    t.ElapsedMs = 0;
                    break;
            }
            Save();
            value = TimerValue(t);
        }
        _live.Publish($"timer:{name}", value);
    }

    /// <summary>Current text of a counter ("0" if never used), for {contatore:NAME}.</summary>
    public string CounterText(string name)
    {
        double v;
        lock (_gate) _data.Counters.TryGetValue(name.Trim(), out v);
        return v.ToString("0.##", L.Culture);
    }

    public List<string> TimerNames()
    {
        lock (_gate) return _data.Timers.Keys.ToList();
    }

    /// <summary>Current time shown by a timer (computed now), for {timer:NAME}.</summary>
    public string TimerText(string name)
    {
        lock (_gate)
            return J.Str((_data.Timers.TryGetValue(name.Trim(), out var t) ? TimerValue(t) : TimerValue(new TimerState()))["text"]) ?? "";
    }

    private static long Elapsed(TimerState t) => t.ElapsedMs + (t.Running ? Math.Max(0, Now - t.StartedAtMs) : 0);

    /// <summary>The client extrapolates the elapsed time while running, so no per-second broadcast is needed.</summary>
    private static JsonObject TimerValue(TimerState t)
    {
        var elapsed = Elapsed(t);
        if (t.Mode == "countdown") elapsed = Math.Min(elapsed, t.DurationMs);
        var down = t.Mode == "countdown";
        var shown = down ? t.DurationMs - elapsed : elapsed;
        return new JsonObject
        {
            ["text"] = FormatMs(shown, down),
            ["value"] = down ? Math.Ceiling(shown / 1000.0) : Math.Floor(shown / 1000.0),
            ["state"] = t.Running,
            ["timer"] = new JsonObject
            {
                ["mode"] = t.Mode,
                ["durationMs"] = t.DurationMs,
                ["elapsedMs"] = elapsed,
                ["running"] = t.Running,
            },
        };
    }

    /// <summary>Countdowns round up, stopwatches round down (same as the webapp).</summary>
    public static string FormatMs(long ms, bool roundUp = true)
    {
        var s = (long)(roundUp ? Math.Ceiling(ms / 1000.0) : Math.Floor(ms / 1000.0));
        return s >= 3600 ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : $"{s / 60:00}:{s % 60:00}";
    }

    /// <summary>Stops countdowns that reached zero and raises <see cref="TimerFinished"/>.</summary>
    public void Tick()
    {
        var done = new List<(string, JsonObject)>();
        var alerts = new List<(string Name, bool Pc, bool Phone)>();
        lock (_gate)
        {
            foreach (var (name, t) in _data.Timers)
            {
                if (!t.Running || t.Mode != "countdown") continue;
                var overdue = Elapsed(t) - t.DurationMs;
                if (overdue < 0) continue;
                t.Running = false;
                t.ElapsedMs = t.DurationMs;
                done.Add(($"timer:{name}", TimerValue(t)));
                // A countdown that ended long ago (PC off or PadNook closed) is stopped silently.
                if (overdue < 60_000) alerts.Add((name, t.AlertPc, t.AlertPhone));
            }
            if (done.Count > 0) Save();
        }
        if (done.Count > 0) _live.PublishMany(done);
        foreach (var (name, pc, phone) in alerts)
        {
            try { TimerFinished?.Invoke(name, pc, phone); }
            catch (Exception ex) { Log.Error("Timer alert failed", ex); }
        }
    }

    /// <summary>
    /// Name shown in the "timer finished" alerts: the label of a key showing the timer (without {placeholders}),
    /// else the name of its page/folder, else "Timer".
    /// </summary>
    public static string TimerTitle(JsonArray profiles, string name)
    {
        var source = $"timer:{name}";
        string? pageName = null;
        foreach (var profile in profiles.OfType<JsonObject>())
            foreach (var page in (profile["pages"] as JsonArray ?? []).OfType<JsonObject>())
                foreach (var key in (page["keys"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    var shows = J.Str(key["live"]?["source"]) == source;
                    var press = key["onPress"] as JsonObject;
                    if (!shows && !(J.Str(press?["type"]) == "timer" && J.Str(press?["name"]) == name)) continue;
                    var label = System.Text.RegularExpressions.Regex.Replace(J.Str(key["style"]?["label"]?["text"]) ?? "", @"\{[a-zA-Z]+\}", " ");
                    label = System.Text.RegularExpressions.Regex.Replace(label, @"\s+", " ").Trim();
                    if (label.Length > 0) return label;
                    pageName ??= J.Str(page["name"]);
                }
        return string.IsNullOrWhiteSpace(pageName) ? "Timer" : pageName.Trim();
    }

    public void PublishAll()
    {
        var items = new List<(string, JsonObject)>();
        lock (_gate)
        {
            foreach (var (n, v) in _data.Counters) items.Add(($"counter:{n}", CounterValue(v)));
            foreach (var (n, t) in _data.Timers) items.Add(($"timer:{n}", TimerValue(t)));
        }
        _live.PublishMany(items);
    }

    /// <summary>Default values for referenced counters/timers that were never used.</summary>
    public JsonObject? DefaultFor(string source)
    {
        if (source.StartsWith("counter:", StringComparison.Ordinal)) return CounterValue(0);
        if (source.StartsWith("timer:", StringComparison.Ordinal)) return TimerValue(new TimerState());
        return null;
    }

    private static string Clean(string name)
    {
        name = name.Trim();
        if (name.Length == 0) throw new Actions.ActionException(L.T("Nome del contatore/timer mancante"));
        return name.Length > 60 ? name[..60] : name;
    }

    private void Save() => _saveTimer.Change(1000, Timeout.Infinite);

    public void Flush()
    {
        string json;
        lock (_gate) json = JsonSerializer.Serialize(_data);
        try { J.WriteAtomic(_file, json); }
        catch (Exception ex) { Log.Error("Unable to save live-state.json", ex); }
    }

    public void Dispose()
    {
        _saveTimer.Dispose();
        Flush();
    }
}
