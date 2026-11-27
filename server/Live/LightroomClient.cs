using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using PadNook.Actions;
using PadNook.Core;

namespace PadNook.Live;

/// <summary>
/// Lightroom Classic through the PadNook plugin (Lightroom/plugin/*.lua, installed by the editor).
/// The plugin listens on 127.0.0.1: 58873 receives commands, 58874 sends the state; PadNook connects to both
/// and retries every 3 s, so it reconnects when Lightroom is reopened. Lines of TAB-separated fields.
/// Live sources: lr.conn, lr.module, lr.param:NAME, lr.pct:NAME (slider level), lr.rating, lr.flag, lr.label.
/// </summary>
public sealed class LightroomClient : IDisposable
{
    public const int CommandPort = 58873;
    public const int StatePort = 58874;

    /// <summary>Develop parameters: PadNook name -> (Italian label, default range, decimals, default step).</summary>
    public static readonly Dictionary<string, (string Label, double Min, double Max, int Decimals, double Step)> Params = new()
    {
        ["Exposure"] = ("Esposizione", -5, 5, 2, 0.05),
        ["Contrast"] = ("Contrasto", -100, 100, 0, 5),
        ["Highlights"] = ("Luci", -100, 100, 0, 5),
        ["Shadows"] = ("Ombre", -100, 100, 0, 5),
        ["Whites"] = ("Bianchi", -100, 100, 0, 5),
        ["Blacks"] = ("Neri", -100, 100, 0, 5),
        ["Temperature"] = ("Temperatura", 2000, 50000, 0, 100),
        ["Tint"] = ("Tinta", -150, 150, 0, 2),
        ["Vibrance"] = ("Vividezza", -100, 100, 0, 5),
        ["Saturation"] = ("Saturazione", -100, 100, 0, 5),
        ["Texture"] = ("Texture", -100, 100, 0, 5),
        ["Clarity"] = ("Chiarezza", -100, 100, 0, 5),
        ["Dehaze"] = ("Foschia", -100, 100, 0, 5),
    };

    private static readonly CultureInfo It = CultureInfo.GetCultureInfo("it-IT");

    private readonly LiveService _live;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, (double Min, double Max)> _ranges = new();
    private readonly ConcurrentDictionary<string, double> _values = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private StreamWriter? _writer;
    private volatile bool _connected;
    private TaskCompletionSource<string[]>? _presetsWait;
    private string? _lastError;

    public LightroomClient(LiveService live)
    {
        _live = live;
        _live.ReferencedChanged += refs => { _ = SendWatchAsync(); };
    }

    public bool Connected => _connected;
    public string? LastError => _lastError;

    public void Start() => _ = Task.Run(() => LoopAsync(_cts.Token));

    private async Task LoopAsync(CancellationToken ct)
    {
        Disconnected();
        while (!ct.IsCancellationRequested)
        {
            try { await RunAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException) { }
            catch (Exception ex) { Log.Error("Lightroom connection failed", ex); }
            Disconnected();
            try { await Task.Delay(TimeSpan.FromSeconds(3), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var cmd = new TcpClient();
        using var state = new TcpClient();
        using (var t = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            t.CancelAfter(TimeSpan.FromSeconds(2));
            await cmd.ConnectAsync("127.0.0.1", CommandPort, t.Token);
            await state.ConnectAsync("127.0.0.1", StatePort, t.Token);
        }
        cmd.NoDelay = true;
        _writer = new StreamWriter(cmd.GetStream(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        _connected = true;
        _lastError = null;
        _live.Publish("lr.conn", LiveService.V(L.T("Connesso"), state: true));
        await SendAsync("hello");
        await SendWatchAsync();

        using var reader = new StreamReader(state.GetStream(), new UTF8Encoding(false));
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            try { OnLine(line); }
            catch (Exception ex) { Log.Error($"Lightroom message failed: {line}", ex); }
        }
    }

    private void Disconnected()
    {
        _connected = false;
        _writer = null;
        _live.Publish("lr.conn", LiveService.V(L.T("Lightroom non connesso"), state: false));
        foreach (var src in _live.Referenced.Where(s => s.StartsWith("lr.", StringComparison.Ordinal) && s != "lr.conn"))
            _live.Publish(src, LiveService.V(L.T("Lightroom non connesso"), state: false));
    }

    private void OnLine(string line)
    {
        var f = line.Split('\t');
        switch (f[0])
        {
            case "val" when f.Length >= 3 && Parse(f[2]) is { } v:
                _values[f[1]] = v;
                PublishParam(f[1], v);
                break;
            case "range" when f.Length >= 4 && Parse(f[2]) is { } min && Parse(f[3]) is { } max && max > min:
                _ranges[f[1]] = (min, max);
                if (_values.TryGetValue(f[1], out var cur)) PublishParam(f[1], cur);
                break;
            case "photo":
                PublishPhoto(f);
                break;
            case "module" when f.Length >= 2:
                _live.Publish("lr.module", LiveService.V(ModuleName(f[1]), sub: f[1]));
                break;
            case "presets":
                _presetsWait?.TrySetResult(f.Skip(1).Where(s => s.Length > 0).ToArray());
                break;
            case "err" when f.Length >= 2:
                _lastError = string.Join(' ', f.Skip(1));
                Log.Info($"Lightroom: {_lastError}");
                break;
        }
    }

    private static double? Parse(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    public (double Min, double Max) Range(string param) =>
        _ranges.TryGetValue(param, out var r) ? r : Params.TryGetValue(param, out var p) ? (p.Min, p.Max) : (-100, 100);

    public static string Format(string param, double v)
    {
        var dec = Params.TryGetValue(param, out var p) ? p.Decimals : 2;
        var text = Math.Round(v, dec).ToString(dec == 0 ? "0" : "0." + new string('0', dec), L.Culture);
        return param == "Temperature" ? $"{text} K" : v > 0 ? $"+{text}" : text;
    }

    private void PublishParam(string param, double v)
    {
        var label = Params.TryGetValue(param, out var p) ? L.T(p.Label) : param;
        var (min, max) = Range(param);
        _live.Publish($"lr.param:{param}", LiveService.V(Format(param, v), v, sub: label));
        var pct = Math.Clamp((v - min) / (max - min) * 100, 0, 100);
        var o = LiveService.V(Format(param, v), pct, sub: label);
        // The phone shows the dragged value in the parameter's unit.
        o["min"] = min;
        o["max"] = max;
        o["decimals"] = Params.TryGetValue(param, out var pp) ? pp.Decimals : 2;
        o["unit"] = param == "Temperature" ? " K" : "";
        _live.Publish($"lr.pct:{param}", o);
    }

    private void PublishPhoto(string[] f)
    {
        if (f.Length < 4)
        {
            _live.Publish("lr.rating", LiveService.V("—", 0, sub: L.T("nessuna foto")));
            _live.Publish("lr.flag", LiveService.V("—", 0, state: false));
            _live.Publish("lr.label", LiveService.V("—"));
            return;
        }
        var rating = (int)Math.Clamp(Parse(f[1]) ?? 0, 0, 5);
        _live.Publish("lr.rating", LiveService.V(new string('★', rating) + new string('☆', 5 - rating), rating));
        var pick = (int)(Parse(f[2]) ?? 0);
        _live.Publish("lr.flag", LiveService.V(pick > 0 ? L.T("Scelta") : pick < 0 ? L.T("Rifiutata") : L.T("Senza bandierina"), pick, state: pick > 0));
        _live.Publish("lr.label", LiveService.V(LabelName(f[3]), sub: f[3]));
    }

    private static string LabelName(string l) => l switch
    {
        "red" => L.T("Rosso"),
        "yellow" => L.T("Giallo"),
        "green" => L.T("Verde"),
        "blue" => L.T("Blu"),
        "purple" => L.T("Viola"),
        _ => L.T("Nessuna"),
    };

    private static string ModuleName(string m) => m switch
    {
        "library" => L.T("Libreria"),
        "develop" => L.T("Sviluppo"),
        "map" => L.T("Mappa"),
        "book" => L.T("Libro"),
        "slideshow" => L.T("Presentazione"),
        "print" => L.T("Stampa"),
        "web" => "Web",
        _ => m,
    };

    // ---- Commands -----------------------------------------------------------------------------

    public async Task SendAsync(string line)
    {
        var w = _writer ?? throw new ActionException(L.T("Lightroom non connesso: apri Lightroom Classic con il plugin PadNook"));
        await _sendGate.WaitAsync();
        try { await w.WriteLineAsync(line); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { throw new ActionException(L.T("Lightroom non connesso")); }
        finally { _sendGate.Release(); }
    }

    private Task SendWatchAsync()
    {
        if (!_connected) return Task.CompletedTask;
        var names = _live.Referenced
            .Where(s => s.StartsWith("lr.param:", StringComparison.Ordinal) || s.StartsWith("lr.pct:", StringComparison.Ordinal))
            .Select(s => s[(s.IndexOf(':') + 1)..]).Distinct();
        return SendAsync($"watch\t{string.Join(',', names)}").ContinueWith(_ => { });
    }

    public static string CheckParam(string? param) =>
        param is { Length: > 0 } p && Params.ContainsKey(p) ? p : throw new ActionException(L.T("Parametro di Lightroom non valido"));

    public static string Num(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>Slider: percentage of the parameter's range.</summary>
    public async Task<JsonObject> SetPercentAsync(string param, double pct)
    {
        var (min, max) = Range(param);
        var v = min + Math.Clamp(pct, 0, 100) / 100 * (max - min);
        var dec = Params.TryGetValue(param, out var p) ? p.Decimals : 2;
        v = Math.Round(v, Math.Max(dec, 0));
        await SendAsync($"set\t{param}\t{Num(v)}");
        _values[param] = v;
        PublishParam(param, v);
        return (JsonObject)_live.Snapshot()[$"lr.pct:{param}"]!.DeepClone();
    }

    public async Task<string[]> PresetsAsync()
    {
        var tcs = _presetsWait = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        await SendAsync("presets");
        try { return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException) { throw new ActionException(L.T("Lightroom non ha risposto con l'elenco dei preset")); }
    }

    public JsonObject Info() => new()
    {
        ["connected"] = _connected,
        ["error"] = _lastError,
        ["installed"] = PluginInstalled,
        ["folder"] = PluginFolder,
    };

    // ---- Plugin installation ------------------------------------------------------------------

    /// <summary>Lightroom Classic loads plugins in this folder automatically (if not, add it from the Plug-in Manager).</summary>
    public static string PluginFolder =>
        Environment.GetEnvironmentVariable("PADNOOK_DEV") == "1" && Environment.GetEnvironmentVariable("PADNOOK_DEV_LR_PLUGIN_DIR") is { Length: > 0 } dev
            ? dev
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Adobe", "Lightroom", "Modules", "PadNook.lrplugin");

    public static bool PluginInstalled => File.Exists(Path.Combine(PluginFolder, "Info.lua"));

    /// <summary>Copies the Lua files embedded in the exe into the plugin folder.</summary>
    public static void InstallPlugin()
    {
        var asm = Assembly.GetExecutingAssembly();
        var names = asm.GetManifestResourceNames().Where(n => n.Contains(".Lightroom.plugin.", StringComparison.Ordinal) && n.EndsWith(".lua", StringComparison.Ordinal)).ToList();
        if (names.Count == 0) throw new ActionException(L.T("File del plugin non trovati nell'eseguibile"));
        Directory.CreateDirectory(PluginFolder);
        foreach (var n in names)
        {
            var file = n[(n.IndexOf(".Lightroom.plugin.", StringComparison.Ordinal) + ".Lightroom.plugin.".Length)..];
            using var src = asm.GetManifestResourceStream(n)!;
            using var dst = File.Create(Path.Combine(PluginFolder, file));
            src.CopyTo(dst);
        }
        Log.Info($"Lightroom plugin installed in {PluginFolder}");
    }

    public void Dispose() => _cts.Cancel();
}
