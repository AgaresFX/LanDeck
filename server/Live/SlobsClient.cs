using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using PadNook.Actions;
using PadNook.Config;
using PadNook.Core;

namespace PadNook.Live;

/// <summary>
/// Streamlabs Desktop (an OBS alternative, no obs-websocket) through its Remote Control API: JSON-RPC 2.0,
/// one JSON per line. Default transport: the named pipe "slobs" (on by default, no token for local programs);
/// alternative: the SockJS server on 127.0.0.1:59650 (/api/websocket) with TcpServerService.auth(token).
/// Live sources: slobs.conn, slobs.stream, slobs.rec, slobs.replay, slobs.scene, slobs.mute:SOURCE.
/// </summary>
public sealed class SlobsClient : IDisposable
{
    private const string TokenSecret = "__slobs";
    private static readonly string PipeName =
        Environment.GetEnvironmentVariable("PADNOOK_DEV") == "1" && Environment.GetEnvironmentVariable("PADNOOK_DEV_SLOBS_PIPE") is { Length: > 0 } p ? p : "slobs";

    private readonly ConfigStore _config;
    private readonly SecretStore _secrets;
    private readonly LiveService _live;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private Func<string, Task>? _send;
    private CancellationTokenSource? _connCts;
    private volatile bool _connected;
    private string? _lastError;
    private int _id;
    private DateTimeOffset _nextMutePoll;

    public SlobsClient(ConfigStore config, SecretStore secrets, LiveService live)
    {
        _config = config;
        _secrets = secrets;
        _live = live;
        _live.ReferencedChanged += _ => _nextMutePoll = DateTimeOffset.MinValue;
    }

    public bool Connected => _connected;

    private (bool Enabled, string Mode, int Port) Settings()
    {
        var o = _config.SettingsSnapshot()["slobs"];
        var port = J.Int(o?["port"]) ?? 59650;
        return (J.Bool(o?["enabled"]) ?? true, J.Str(o?["mode"]) == "ws" ? "ws" : "pipe", port is > 0 and < 65536 ? port : 59650);
    }

    public JsonObject PublicSettings()
    {
        var (enabled, mode, port) = Settings();
        return new JsonObject { ["enabled"] = enabled, ["mode"] = mode, ["port"] = port, ["hasToken"] = _secrets.Has(TokenSecret) };
    }

    public void UpdateSettings(bool enabled, string mode, int port, string? token)
    {
        _config.UpdateSettings(s => s["slobs"] = new JsonObject { ["enabled"] = enabled, ["mode"] = mode == "ws" ? "ws" : "pipe", ["port"] = port });
        if (!string.IsNullOrEmpty(token)) _secrets.Set(TokenSecret, token.Trim());
        _connCts?.Cancel();
    }

    public void Start() => _ = Task.Run(() => LoopAsync(_cts.Token));

    private async Task LoopAsync(CancellationToken ct)
    {
        Disconnected();
        while (!ct.IsCancellationRequested)
        {
            var (enabled, mode, port) = Settings();
            if (enabled)
            {
                _connCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                try
                {
                    if (mode == "ws") await RunWebSocketAsync(port, _connCts.Token);
                    else await RunPipeAsync(_connCts.Token);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (OperationCanceledException) { }
                catch (TimeoutException) { _lastError = L.T("Streamlabs Desktop non è aperto"); }
                catch (Exception ex) when (ex is IOException or WebSocketException)
                {
                    _lastError = mode == "ws"
                        ? L.T("Streamlabs Desktop non è aperto o il server WebSocket è disattivato (Impostazioni → Remote Control)")
                        : L.T("Streamlabs Desktop non è aperto");
                }
                catch (ActionException ex) { _lastError = ex.Message; }
                catch (Exception ex)
                {
                    _lastError = ex.Message;
                    Log.Error("Streamlabs Desktop connection failed", ex);
                }
                Disconnected();
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void Disconnected()
    {
        _connected = false;
        _send = null;
        foreach (var p in _pending.Values) p.TrySetCanceled();
        _pending.Clear();
        _live.Publish("slobs.conn", LiveService.V(L.T("Non connesso"), state: false));
        foreach (var k in new[] { "slobs.stream", "slobs.rec", "slobs.replay" })
            _live.Publish(k, LiveService.V(L.T("Streamlabs chiuso"), state: false));
    }

    // ---- Transports ---------------------------------------------------------------------------

    private async Task RunPipeAsync(CancellationToken ct)
    {
        await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(1500, ct); // TimeoutException when Streamlabs is closed
        using var reader = new StreamReader(pipe, new UTF8Encoding(false));
        var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        _send = line => writer.WriteLineAsync(line.AsMemory(), ct);
        var receive = Task.Run(async () =>
        {
            while (await reader.ReadLineAsync(ct) is { } line) OnLine(line);
        }, ct);
        await OnConnectedAsync(needsAuth: false);
        await receive;
    }

    private async Task RunWebSocketAsync(int port, CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        using (var t = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            t.CancelAfter(TimeSpan.FromSeconds(3));
            // SockJS also offers a plain WebSocket endpoint at <prefix>/websocket.
            await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/api/websocket"), t.Token);
        }
        _send = async line =>
        {
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        };
        var receive = Task.Run(async () =>
        {
            var buffer = new byte[64 * 1024];
            var sb = new StringBuilder();
            while (ws.State == WebSocketState.Open)
            {
                var r = await ws.ReceiveAsync(buffer, ct);
                if (r.MessageType == WebSocketMessageType.Close) return;
                sb.Append(Encoding.UTF8.GetString(buffer, 0, r.Count));
                if (!r.EndOfMessage) continue;
                foreach (var line in sb.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)) OnLine(line);
                sb.Clear();
            }
        }, ct);
        await OnConnectedAsync(needsAuth: true);
        await receive;
    }

    // ---- JSON-RPC -----------------------------------------------------------------------------

    private void OnLine(string line)
    {
        JsonObject? msg;
        try { msg = JsonNode.Parse(line) as JsonObject; }
        catch { return; }
        if (msg is null) return;
        var result = msg["result"];
        // Events: a response with id null and result {_type:"EVENT", resourceId:"Service.event", data}.
        if (result is JsonObject r && J.Str(r["_type"]) == "EVENT")
        {
            try { OnEvent(J.Str(r["resourceId"]) ?? "", r["data"]); }
            catch (Exception ex) { Log.Error("Streamlabs Desktop event failed", ex); }
            return;
        }
        var id = msg["id"]?.ToString();
        if (id is null || !_pending.TryRemove(id, out var tcs)) return;
        if (msg["error"] is JsonObject err)
        {
            var text = J.Str(err["message"]) ?? L.F("errore {0}", J.Int(err["code"]));
            tcs.TrySetException(new ActionException(text.Contains("Invalid token", StringComparison.OrdinalIgnoreCase)
                ? L.T("Streamlabs Desktop: token errato (Impostazioni → Remote Control)") : $"Streamlabs Desktop: {text}"));
        }
        else tcs.TrySetResult(result?.DeepClone());
    }

    /// <summary>Calls method on resource ("StreamingService", "AudioSource[\"id\"]"…) and returns the result.</summary>
    public async Task<JsonNode?> CallAsync(string resource, string method, params JsonNode?[] args)
    {
        var send = _send ?? throw new ActionException(_lastError is null ? L.T("Streamlabs Desktop non connesso") : L.F("Streamlabs Desktop non connesso: {0}", _lastError));
        var id = Interlocked.Increment(ref _id).ToString();
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var req = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = new JsonObject { ["resource"] = resource, ["args"] = new JsonArray(args) },
        };
        await _sendGate.WaitAsync();
        try { await send(req.ToJsonString()); }
        finally { _sendGate.Release(); }
        try { return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException)
        {
            _pending.TryRemove(id, out _);
            throw new ActionException(L.T("Streamlabs Desktop non risponde"));
        }
    }

    private async Task OnConnectedAsync(bool needsAuth)
    {
        if (needsAuth)
        {
            var token = _secrets.Get(TokenSecret) ?? throw new ActionException(L.T("Manca il token di Streamlabs Desktop (Impostazioni → Remote Control)"));
            await CallAsync("TcpServerService", "auth", token);
        }
        // Subscriptions: calling an event property returns a SUBSCRIPTION; events follow.
        foreach (var (res, ev) in new[]
                 {
                     ("StreamingService", "streamingStatusChange"), ("StreamingService", "recordingStatusChange"),
                     ("StreamingService", "replayBufferStatusChange"), ("ScenesService", "sceneSwitched"),
                 })
        {
            try { await CallAsync(res, ev); }
            catch (ActionException ex) { Log.Info($"Streamlabs Desktop: no {ev} ({ex.Message})"); }
        }
        _connected = true;
        _lastError = null;
        _live.Publish("slobs.conn", LiveService.V(L.T("Connesso"), state: true));
        var model = await CallAsync("StreamingService", "getModel") as JsonObject;
        PublishStream(J.Str(model?["streamingStatus"]));
        PublishRec(J.Str(model?["recordingStatus"]));
        PublishReplay(J.Str(model?["replayBufferStatus"]));
        var scene = await CallAsync("ScenesService", "activeScene") as JsonObject;
        PublishScene(J.Str(scene?["name"]));
        _nextMutePoll = DateTimeOffset.MinValue;
        _ = MuteLoopAsync(_connCts!.Token);
    }

    private void OnEvent(string resourceId, JsonNode? data)
    {
        switch (resourceId)
        {
            case "StreamingService.streamingStatusChange": PublishStream(J.Str(data)); break;
            case "StreamingService.recordingStatusChange": PublishRec(J.Str(data)); break;
            case "StreamingService.replayBufferStatusChange": PublishReplay(J.Str(data)); break;
            case "ScenesService.sceneSwitched": PublishScene(J.Str(data?["name"])); break;
        }
    }

    private void PublishStream(string? s) => _live.Publish("slobs.stream", s switch
    {
        "live" => LiveService.V(L.T("IN DIRETTA"), state: true),
        "starting" => LiveService.V(L.T("Avvio…"), state: true),
        "ending" => LiveService.V(L.T("Chiusura…"), state: false),
        "reconnecting" => LiveService.V(L.T("Riconnessione…"), state: true),
        _ => LiveService.V("Offline", state: false),
    });

    private void PublishRec(string? s) => _live.Publish("slobs.rec", s switch
    {
        "recording" or "starting" or "start" => LiveService.V("REC", state: true),
        "stopping" or "writing" => LiveService.V(L.T("Salvataggio…"), state: false),
        _ => LiveService.V(L.T("Fermo"), state: false),
    });

    private void PublishReplay(string? s) => _live.Publish("slobs.replay", s switch
    {
        "running" => LiveService.V(L.T("Replay attivo"), state: true),
        "saving" or "writing" => LiveService.V(L.T("Salvataggio…"), state: true),
        "wrote" => LiveService.V(L.T("Salvato"), state: true),
        _ => LiveService.V(L.T("Replay spento"), state: false),
    });

    private void PublishScene(string? name) => _live.Publish("slobs.scene", LiveService.V(name ?? "—"));

    /// <summary>Streamlabs sends no mute events to API clients: poll every 3 s (the API allows ~2 calls/s).</summary>
    private async Task MuteLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _connected)
        {
            try { await Task.Delay(500, ct); }
            catch (OperationCanceledException) { return; }
            var refs = _live.Referenced.Where(s => s.StartsWith("slobs.mute:", StringComparison.Ordinal)).ToList();
            if (refs.Count == 0 || !_live.HasClients || DateTimeOffset.Now < _nextMutePoll) continue;
            _nextMutePoll = DateTimeOffset.Now.AddSeconds(3);
            try
            {
                var sources = await AudioSourcesAsync();
                foreach (var r in refs)
                {
                    var src = sources.FirstOrDefault(x => string.Equals(x.Name, r[11..], StringComparison.OrdinalIgnoreCase));
                    _live.Publish(r, src.Id is null ? LiveService.V("—", state: false, sub: L.T("sorgente non trovata"))
                        : LiveService.V(src.Muted ? L.T("Muto") : L.T("Attivo"), state: src.Muted));
                }
            }
            catch (ActionException) { }
        }
    }

    // ---- Commands -----------------------------------------------------------------------------

    private async Task<List<(string? Id, string Name, bool Muted)>> AudioSourcesAsync()
    {
        var arr = await CallAsync("AudioService", "getSources") as JsonArray ?? [];
        return arr.OfType<JsonObject>().Select(o =>
        {
            // Helpers nested in arrays are sent as plain objects: accept the model fields or the internal ones.
            var inner = o["audioSource"] as JsonObject ?? o["source"] as JsonObject;
            var id = J.Str(o["sourceId"]) ?? J.Str(o["id"]) ?? SourceIdFromResource(J.Str(o["resourceId"])) ?? J.Str(inner?["sourceId"]);
            return (id, J.Str(o["name"]) ?? J.Str(inner?["name"]) ?? "", J.Bool(o["muted"]) ?? J.Bool(inner?["muted"]) ?? false);
        }).ToList();
    }

    private static string? SceneName(JsonObject s) => J.Str(s["name"]) ?? J.Str(s["scene"]?["name"]);
    private static string? SceneId(JsonObject s) => J.Str(s["id"]) ?? J.Str(s["sceneId"]) ?? J.Str(s["scene"]?["id"]) ?? SourceIdFromResource(J.Str(s["resourceId"]));

    private static string? SourceIdFromResource(string? resourceId)
    {
        // e.g. AudioSource["ffmpeg_source_1234"]
        var i = resourceId?.IndexOf('[') ?? -1;
        if (i < 0) return null;
        try { return J.Str((JsonNode.Parse(resourceId![i..]) as JsonArray)?[0]); }
        catch { return null; }
    }

    public async Task RunAsync(string command, string scene, string input)
    {
        switch (command)
        {
            case "streamToggle": await CallAsync("StreamingService", "toggleStreaming"); break;
            case "streamStart":
            case "streamStop":
            {
                var model = await CallAsync("StreamingService", "getModel") as JsonObject;
                var live = J.Str(model?["streamingStatus"]) is "live" or "starting" or "reconnecting";
                if (live != (command == "streamStart")) await CallAsync("StreamingService", "toggleStreaming");
                break;
            }
            case "recordToggle": await CallAsync("StreamingService", "toggleRecording"); break;
            case "replaySave": await CallAsync("StreamingService", "saveReplay"); break;
            case "replayToggle":
            {
                var model = await CallAsync("StreamingService", "getModel") as JsonObject;
                var running = J.Str(model?["replayBufferStatus"]) is "running" or "saving" or "writing" or "wrote";
                await CallAsync("StreamingService", running ? "stopReplayBuffer" : "startReplayBuffer");
                break;
            }
            case "scene":
            {
                if (scene.Length == 0) throw new ActionException(L.T("Nessuna scena scelta"));
                var scenes = await CallAsync("ScenesService", "getScenes") as JsonArray ?? [];
                var match = scenes.OfType<JsonObject>().FirstOrDefault(s => string.Equals(SceneName(s), scene, StringComparison.OrdinalIgnoreCase))
                            ?? throw new ActionException(L.F("Scena «{0}» non trovata in Streamlabs Desktop", scene));
                await CallAsync("ScenesService", "makeSceneActive", SceneId(match));
                break;
            }
            case "muteToggle":
            {
                if (input.Length == 0) throw new ActionException(L.T("Nessuna sorgente audio scelta"));
                var src = (await AudioSourcesAsync()).FirstOrDefault(s => string.Equals(s.Name, input, StringComparison.OrdinalIgnoreCase));
                if (src.Id is null) throw new ActionException(L.F("Sorgente audio «{0}» non trovata in Streamlabs Desktop", input));
                // Resource = helper name + JSON array of constructor args, e.g. AudioSource["mic_1"].
                await CallAsync($"AudioSource{new JsonArray(src.Id).ToJsonString()}", "setMuted", !src.Muted);
                _live.Publish($"slobs.mute:{input}", LiveService.V(!src.Muted ? L.T("Muto") : L.T("Attivo"), state: !src.Muted));
                _nextMutePoll = DateTimeOffset.Now.AddSeconds(1);
                break;
            }
            case "recordPause": throw new ActionException(L.T("Streamlabs Desktop non permette di mettere in pausa la registrazione dall'API"));
            case "virtualCam": throw new ActionException(L.T("Streamlabs Desktop non permette di comandare la videocamera virtuale dall'API"));
            default: throw new ActionException(L.T("Comando non valido per Streamlabs Desktop"));
        }
    }

    /// <summary>Scene and audio source names for the editor.</summary>
    public async Task<JsonObject> InfoAsync()
    {
        var o = PublicSettings();
        o["connected"] = _connected;
        o["error"] = _lastError;
        if (!_connected) return o;
        try
        {
            var scenes = await CallAsync("ScenesService", "getScenes") as JsonArray ?? [];
            o["scenes"] = new JsonArray(scenes.OfType<JsonObject>().Select(s => (JsonNode?)JsonValue.Create(SceneName(s))).ToArray());
            o["inputs"] = new JsonArray((await AudioSourcesAsync()).Select(s => (JsonNode?)JsonValue.Create(s.Name)).ToArray());
        }
        catch (Exception ex) { o["error"] = ex.Message; }
        return o;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _connCts?.Cancel();
    }
}
