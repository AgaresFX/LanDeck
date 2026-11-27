using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using PadNook.Actions;
using PadNook.Config;
using PadNook.Core;

namespace PadNook.Live;

/// <summary>
/// HTTP dynamic keys: live source "http:KEYID" with the request in key.live.http
/// ({ ...request spec, intervalSec, path }). Polled only while a device is connected.
/// </summary>
public sealed class HttpPoller : IDisposable
{
    private const int MinInterval = 5;

    private sealed class Entry
    {
        public required JsonObject Spec;
        public required string Json;
        public int IntervalSec;
        public string? Path;
        public DateTimeOffset Due;
        public DateTimeOffset LastRun;
        public bool Running;
    }

    private readonly ConfigStore _config;
    private readonly LiveService _live;
    private readonly HttpEngine _engine;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    public HttpPoller(ConfigStore config, LiveService live, HttpEngine engine)
    {
        _config = config;
        _live = live;
        _engine = engine;
        config.ProfilesChanged += Rescan;
        Rescan();
    }

    public void Start() => _ = Task.Run(() => LoopAsync(_cts.Token));

    /// <summary>Re-polls soon every source not polled in the last 2 seconds (after an HTTP action).</summary>
    public void Kick()
    {
        var now = DateTimeOffset.Now;
        foreach (var e in _entries.Values)
            if (now - e.LastRun > TimeSpan.FromSeconds(2)) e.Due = now.AddMilliseconds(400);
    }

    private void Rescan()
    {
        var found = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        Walk(_config.Snapshot().Profiles, found);
        foreach (var key in _entries.Keys.Where(k => !found.ContainsKey(k)).ToList())
        {
            _entries.TryRemove(key, out _);
            _live.Remove(key);
        }
        foreach (var (source, http) in found)
        {
            var json = http.ToJsonString();
            if (_entries.TryGetValue(source, out var old) && old.Json == json) continue;
            _entries[source] = new Entry
            {
                Spec = http,
                Json = json,
                IntervalSec = Math.Clamp(J.Int(http["intervalSec"]) ?? 30, MinInterval, 86400),
                Path = J.Str(http["path"]),
                // Startup: poll now. Edited in the editor: wait until the typing stops.
                Due = old is null ? DateTimeOffset.MinValue : DateTimeOffset.Now.AddSeconds(1.5),
            };
            if (old is null) _live.Publish(source, LiveService.V("…"));
        }
    }

    private static void Walk(JsonNode? node, Dictionary<string, JsonObject> found)
    {
        switch (node)
        {
            case JsonObject o:
                if (o["live"] is JsonObject live && J.Str(live["source"]) is { } src && src.StartsWith("http:", StringComparison.Ordinal)
                    && live["http"] is JsonObject http && !found.ContainsKey(src))
                    found[src] = (JsonObject)http.DeepClone();
                foreach (var (_, child) in o) Walk(child, found);
                break;
            case JsonArray a:
                foreach (var child in a) Walk(child, found);
                break;
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(250, ct); }
            catch (OperationCanceledException) { return; }
            if (!_live.HasClients) continue;
            var now = DateTimeOffset.Now;
            foreach (var (source, e) in _entries)
            {
                if (e.Running || e.Due > now) continue;
                e.Running = true;
                e.LastRun = now;
                e.Due = now.AddSeconds(e.IntervalSec);
                _ = PollAsync(source, e, ct);
            }
        }
    }

    private async Task PollAsync(string source, Entry e, CancellationToken ct)
    {
        JsonObject value;
        try { value = HttpEngine.ToLive(await _engine.SendAsync(e.Spec, ct), e.Path); }
        catch (ActionException ex) { value = HttpEngine.ErrorLive(ex.Message); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            Log.Error($"HTTP poll {source} failed", ex);
            value = HttpEngine.ErrorLive(ex.Message);
        }
        finally { e.Running = false; }
        // Skip if the key was removed or changed meanwhile.
        if (_entries.TryGetValue(source, out var cur) && ReferenceEquals(cur, e)) _live.Publish(source, value);
    }

    /// <summary>Editor "Prova": runs a request once and describes the result.</summary>
    public async Task<JsonObject> TestAsync(JsonObject spec)
    {
        try
        {
            var r = await _engine.SendAsync(spec);
            var path = J.Str(spec["path"]);
            var extracted = HttpEngine.Extract(r.Body, path, out var found);
            return new JsonObject
            {
                ["ok"] = r.Status is >= 200 and < 300,
                ["status"] = r.Status,
                ["ms"] = r.Ms,
                ["contentType"] = r.ContentType,
                ["body"] = r.Body.Length > 2000 ? r.Body[..2000] + "…" : r.Body,
                ["live"] = HttpEngine.ToLive(r, path),
                ["pathFound"] = string.IsNullOrWhiteSpace(path) || found,
                ["extracted"] = extracted?.ToJsonString(),
            };
        }
        catch (ActionException ex)
        {
            return new JsonObject { ["ok"] = false, ["status"] = 0, ["error"] = ex.Message };
        }
    }

    public void Dispose() => _cts.Cancel();
}
