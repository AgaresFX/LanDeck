using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PadNook.Actions;
using PadNook.Core;
using PadNook.Live;

namespace PadNook.Plugins;

/// <summary>
/// Script plugins: every folder in %APPDATA%\PadNook\plugins with a plugin.json manifest.
/// A plugin declares actions (run a program/script with the values typed in the editor) and
/// live sources (a program whose output is shown on dynamic keys). See PLUGIN.md for the format.
/// </summary>
public sealed partial class PluginHost : IDisposable
{
    public sealed record Plugin(string Id, string Dir, JsonObject Manifest);

    private readonly string _dir;
    private readonly LiveService _live;
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _lastRun = new();
    private readonly HashSet<string> _running = new();
    private readonly CancellationTokenSource _cts = new();
    private Dictionary<string, Plugin> _plugins = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _errors = new();

    public event Action? Reloaded;

    public string Folder => _dir;

    public PluginHost(AppPaths paths, LiveService live)
    {
        _dir = Path.Combine(paths.Root, "plugins");
        _live = live;
        var firstTime = !Directory.Exists(_dir);
        Directory.CreateDirectory(_dir);
        if (firstTime) PluginExamples.Write(_dir);
        Reload();
        _ = Task.Run(() => SourceLoopAsync(_cts.Token));
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.IgnoreCase)]
    private static partial Regex IdPattern();

    public void Reload()
    {
        var plugins = new Dictionary<string, Plugin>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        foreach (var dir in Directory.EnumerateDirectories(_dir))
        {
            var file = Path.Combine(dir, "plugin.json");
            if (!File.Exists(file)) continue;
            try
            {
                var m = JsonNode.Parse(File.ReadAllText(file), documentOptions: new() { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                        ?? throw new InvalidDataException(L.T("il file non contiene un oggetto JSON"));
                var id = J.Str(m["id"]) ?? Path.GetFileName(dir);
                if (!IdPattern().IsMatch(id)) throw new InvalidDataException(L.F("id non valido «{0}» (solo lettere, numeri, . _ -)", id));
                if (plugins.ContainsKey(id)) throw new InvalidDataException(L.F("id «{0}» già usato da un altro plugin", id));
                m["id"] = id;
                plugins[id] = new Plugin(id, dir, m);
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(dir)}: {ex.Message}");
                Log.Info($"Plugin {dir} not loaded: {ex.Message}");
            }
        }
        lock (_gate)
        {
            _plugins = plugins;
            _errors = errors;
            _lastRun.Clear();
        }
        Log.Info($"Plugins loaded: {plugins.Count}");
        Reloaded?.Invoke();
    }

    /// <summary>What the webapp needs to build the editor UI.</summary>
    public JsonObject Describe()
    {
        lock (_gate)
        {
            var list = new JsonArray();
            foreach (var p in _plugins.Values.OrderBy(p => J.Str(p.Manifest["name"]) ?? p.Id))
            {
                var m = p.Manifest;
                list.Add(new JsonObject
                {
                    ["id"] = p.Id,
                    ["name"] = J.Str(m["name"]) ?? p.Id,
                    ["version"] = J.Str(m["version"]),
                    ["author"] = J.Str(m["author"]),
                    ["description"] = J.Str(m["description"]),
                    ["actions"] = new JsonArray((m["actions"] as JsonArray ?? []).OfType<JsonObject>().Select(a => (JsonNode)new JsonObject
                    {
                        ["id"] = J.Str(a["id"]),
                        ["name"] = J.Str(a["name"]) ?? J.Str(a["id"]),
                        ["description"] = J.Str(a["description"]),
                        ["fields"] = a["fields"]?.DeepClone() ?? new JsonArray(),
                    }).ToArray()),
                    ["sources"] = new JsonArray((m["sources"] as JsonArray ?? []).OfType<JsonObject>().Select(s => (JsonNode)new JsonObject
                    {
                        ["id"] = J.Str(s["id"]),
                        ["name"] = J.Str(s["name"]) ?? J.Str(s["id"]),
                        ["description"] = J.Str(s["description"]),
                    }).ToArray()),
                });
            }
            return new JsonObject
            {
                ["plugins"] = list,
                ["errors"] = new JsonArray(_errors.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray()),
            };
        }
    }

    private (Plugin, JsonObject) Lookup(string pluginId, string kind, string itemId)
    {
        lock (_gate)
        {
            if (!_plugins.TryGetValue(pluginId, out var p)) throw new ActionException(L.F("Plugin «{0}» non installato", pluginId));
            var item = (p.Manifest[kind] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(a => J.Str(a["id"]) == itemId)
                       ?? throw new ActionException(L.F("Il plugin «{0}» non ha «{1}»", pluginId, itemId));
            return (p, item);
        }
    }

    // ---- Actions ------------------------------------------------------------------------------

    public async Task RunActionAsync(string pluginId, string actionId, JsonObject? fields)
    {
        var (plugin, action) = Lookup(pluginId, "actions", actionId);
        var values = FieldValues(action, fields);
        var run = action["run"] as JsonObject ?? throw new ActionException(L.T("Azione del plugin senza «run»"));
        var wait = J.Bool(run["wait"]) ?? false;
        var timeout = Math.Clamp(J.Int(run["timeoutSec"]) ?? 30, 1, 600);
        var psi = BuildProcess(plugin, run, values, capture: wait);

        Process? p;
        try { p = Process.Start(psi); }
        catch (Win32Exception ex) { throw new ActionException(L.F("Plugin: impossibile avviare «{0}»: {1}", psi.FileName, ex.Message)); }
        if (p is null) return;
        using (p)
        {
            if (!wait) return;
            var stderr = p.StandardError.ReadToEndAsync();
            var stdout = p.StandardOutput.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch { }
                throw new ActionException(L.F("Plugin: «{0}» non ha terminato entro {1} s", actionId, timeout));
            }
            if (p.ExitCode != 0)
            {
                var err = (await stderr).Trim();
                if (err.Length == 0) err = (await stdout).Trim();
                var line = err.Split('\n').FirstOrDefault()?.Trim();
                throw new ActionException(string.IsNullOrEmpty(line) ? L.F("Plugin: codice di uscita {0}", p.ExitCode) : $"Plugin: {line}");
            }
        }
    }

    private static Dictionary<string, string> FieldValues(JsonObject item, JsonObject? given)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in (item["fields"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var id = J.Str(f["id"]);
            if (string.IsNullOrEmpty(id)) continue;
            var node = given?[id] ?? f["default"];
            values[id] = node switch
            {
                null => "",
                JsonValue v when v.TryGetValue<bool>(out var b) => b ? "true" : "false",
                JsonValue v when v.TryGetValue<double>(out var d) => d.ToString(CultureInfo.InvariantCulture),
                JsonValue v when v.TryGetValue<string>(out var s) => s,
                _ => node.ToJsonString(),
            };
        }
        return values;
    }

    [GeneratedRegex(@"\{([A-Za-z0-9_.-]+)\}")]
    private static partial Regex Placeholder();

    private ProcessStartInfo BuildProcess(Plugin plugin, JsonObject run, Dictionary<string, string> values, bool capture)
    {
        string Sub(string s) => Placeholder().Replace(s, m =>
            m.Groups[1].Value switch
            {
                "pluginDir" => plugin.Dir,
                var k when values.TryGetValue(k, out var v) => v,
                _ => m.Value,
            });

        var file = J.Str(run["file"]) ?? throw new ActionException(L.T("Plugin: «run.file» mancante"));
        file = Environment.ExpandEnvironmentVariables(Sub(file));
        var local = Path.Combine(plugin.Dir, file);
        if (!Path.IsPathRooted(file) && File.Exists(local)) file = local;

        var hidden = J.Bool(run["hidden"]) ?? true;
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = hidden,
            WorkingDirectory = plugin.Dir,
            RedirectStandardOutput = capture,
            RedirectStandardError = capture,
        };
        if (capture)
        {
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
        }
        foreach (var a in (run["args"] as JsonArray ?? []))
            if (J.Str(a) is { } arg) psi.ArgumentList.Add(Sub(arg));
        psi.Environment["PADNOOK_PLUGIN_DIR"] = plugin.Dir;
        psi.Environment["PADNOOK_VERSION"] = Program.Version;
        foreach (var (k, v) in values)
        {
            var id = k.ToUpperInvariant().Replace('-', '_').Replace('.', '_');
            psi.Environment[$"PADNOOK_{id}"] = v;
        }
        return psi;
    }

    // ---- Live sources -------------------------------------------------------------------------

    private async Task SourceLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(1000, ct); }
            catch (OperationCanceledException) { return; }
            foreach (var src in _live.Referenced.Where(s => s.StartsWith("plugin:", StringComparison.Ordinal)))
            {
                var parts = src[7..].Split('/', 2);
                if (parts.Length != 2) continue;
                Plugin plugin;
                JsonObject source;
                try { (plugin, source) = Lookup(parts[0], "sources", parts[1]); }
                catch (ActionException ex)
                {
                    _live.Publish(src, LiveService.V("—", sub: ex.Message));
                    continue;
                }
                var interval = Math.Clamp(J.Num(source["intervalSec"]) ?? 10, 1, 86400);
                lock (_gate)
                {
                    if (_running.Contains(src)) continue;
                    if (_lastRun.TryGetValue(src, out var last) && (DateTime.UtcNow - last).TotalSeconds < interval) continue;
                    _lastRun[src] = DateTime.UtcNow;
                    _running.Add(src);
                }
                _ = Task.Run(async () =>
                {
                    try { _live.Publish(src, await RunSourceAsync(plugin, source)); }
                    catch (Exception ex) { _live.Publish(src, LiveService.V(L.T("Errore"), sub: ex.Message)); }
                    finally { lock (_gate) _running.Remove(src); }
                });
            }
        }
    }

    private async Task<JsonObject> RunSourceAsync(Plugin plugin, JsonObject source)
    {
        var run = source["run"] as JsonObject ?? throw new InvalidDataException(L.T("sorgente senza «run»"));
        var psi = BuildProcess(plugin, run, new(), capture: true);
        psi.CreateNoWindow = true;
        using var p = Process.Start(psi) ?? throw new InvalidOperationException(L.T("processo non avviato"));
        var stdout = p.StandardOutput.ReadToEndAsync();
        _ = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(J.Int(run["timeoutSec"]) ?? 15, 1, 120)));
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            throw new TimeoutException(L.T("tempo scaduto"));
        }
        return ParseOutput((await stdout).Trim());
    }

    /// <summary>Output: a JSON object {text, value, state, sub, icon} or plain text (first line).</summary>
    public static JsonObject ParseOutput(string output)
    {
        if (output.StartsWith('{'))
        {
            try
            {
                if (JsonNode.Parse(output) is JsonObject o)
                {
                    var v = new JsonObject { ["text"] = J.Str(o["text"]) ?? o["value"]?.ToJsonString() ?? "" };
                    if (J.Num(o["value"]) is { } n) v["value"] = n;
                    if (J.Bool(o["state"]) is { } b) v["state"] = b;
                    if (J.Str(o["sub"]) is { } s) v["sub"] = s;
                    if (J.Str(o["icon"]) is { } i) v["icon"] = i;
                    return v;
                }
            }
            catch { /* fall back to plain text */ }
        }
        var line = output.Split('\n').FirstOrDefault()?.Trim() ?? "";
        if (line.Length > 200) line = line[..200];
        var res = new JsonObject { ["text"] = line };
        if (double.TryParse(line.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var num)) res["value"] = num;
        return res;
    }

    public void Dispose() => _cts.Cancel();
}
