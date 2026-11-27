using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using PadNook.Actions;
using PadNook.Config;
using PadNook.Core;

namespace PadNook.Live;

/// <summary>
/// OBS Studio through obs-websocket 5 (built into OBS 28+), on this PC only (127.0.0.1):
/// no firewall rule needed. Live sources: obs.conn, obs.stream, obs.rec, obs.scene, obs.mute:INPUT.
/// </summary>
public sealed class ObsClient : IDisposable
{
    private readonly ConfigStore _config;
    private readonly LiveService _live;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _connCts;
    private volatile bool _identified;
    private string? _lastError;

    private const string SecretName = "__obs";
    private readonly SecretStore _secrets;

    public ObsClient(ConfigStore config, LiveService live, SecretStore secrets)
    {
        _config = config;
        _live = live;
        _secrets = secrets;
        MigratePassword();
        _live.ReferencedChanged += refs => { if (_identified) _ = RefreshMutesAsync(); };
    }

    public bool Connected => _identified;
    public string? LastError => _lastError;

    private (bool Enabled, int Port, string Password) Settings()
    {
        var obs = _config.SettingsSnapshot()["obs"];
        var port = J.Int(obs?["port"]) ?? 4455;
        return (J.Bool(obs?["enabled"]) ?? true, port is > 0 and < 65536 ? port : 4455, _secrets.Get(SecretName) ?? "");
    }

    /// <summary>Up to 1.10: the password was stored in clear text in settings.obs.password.</summary>
    private void MigratePassword()
    {
        if (J.Str(_config.SettingsSnapshot()["obs"]?["password"]) is not { } plain) return;
        if (plain.Length > 0) _secrets.Set(SecretName, plain);
        _config.UpdateSettings(s => (s["obs"] as JsonObject)?.Remove("password"));
        _config.Flush();
        _config.UpdateSettings(_ => { }); // second save: the .bak copy no longer holds the clear password
        _config.Flush();
        Log.Info("OBS password moved to the encrypted store");
    }

    public JsonObject PublicSettings()
    {
        var (enabled, port, password) = Settings();
        return new JsonObject { ["enabled"] = enabled, ["port"] = port, ["hasPassword"] = password.Length > 0 };
    }

    public void UpdateSettings(bool enabled, int port, string? password)
    {
        _config.UpdateSettings(s =>
        {
            var o = s["obs"] as JsonObject ?? new JsonObject();
            o["enabled"] = enabled;
            o["port"] = port;
            o.Remove("password");
            s["obs"] = o;
        });
        if (password is not null)
        {
            if (password.Length > 0) _secrets.Set(SecretName, password);
            else _secrets.Delete(SecretName);
        }
        _connCts?.Cancel(); // reconnect with the new settings
    }

    public void Start() => _ = Task.Run(() => LoopAsync(_cts.Token));

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var (enabled, port, password) = Settings();
            if (enabled)
            {
                try { await RunConnectionAsync(port, password, ct); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { _lastError ??= L.T("OBS non risponde"); }
                catch (OperationCanceledException) { return; }
                catch (WebSocketException) { _lastError = L.T("OBS non è aperto o il server WebSocket è disattivato"); }
                catch (Exception ex) { _lastError = ex.Message; }
                Disconnected();
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void Disconnected()
    {
        _identified = false;
        foreach (var p in _pending.Values) p.TrySetCanceled();
        _pending.Clear();
        _live.Publish("obs.conn", LiveService.V(L.T("Non connesso"), state: false));
        _live.Publish("obs.stream", LiveService.V(L.T("OBS chiuso"), state: false));
        _live.Publish("obs.rec", LiveService.V(L.T("OBS chiuso"), state: false));
    }

    private async Task RunConnectionAsync(int port, string password, CancellationToken appCt)
    {
        _connCts = CancellationTokenSource.CreateLinkedTokenSource(appCt);
        var ct = _connCts.Token;
        using var ws = new ClientWebSocket();
        ws.Options.AddSubProtocol("obswebsocket.json");
        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(3));
            await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), connectTimeout.Token);
        }
        _ws = ws;

        var hello = await ReceiveAsync(ws, ct) ?? throw new IOException(L.T("Nessuna risposta da OBS"));
        var identify = new JsonObject { ["rpcVersion"] = 1, ["eventSubscriptions"] = 1 | 4 | 8 | 64 };
        if (hello["d"]?["authentication"] is JsonObject auth)
        {
            if (password.Length == 0)
            {
                _lastError = L.T("OBS richiede una password: inseriscila nelle impostazioni di PadNook");
                await Task.Delay(TimeSpan.FromSeconds(25), ct);
                return;
            }
            identify["authentication"] = AuthString(password, J.Str(auth["salt"]) ?? "", J.Str(auth["challenge"]) ?? "");
        }
        await SendAsync(new JsonObject { ["op"] = 1, ["d"] = identify });

        var receive = ReceiveLoopAsync(ws, ct);
        await Task.WhenAny(receive, Task.Delay(TimeSpan.FromSeconds(5), ct));
        if (!_identified && !receive.IsCompleted)
        {
            _lastError = L.T("OBS ha rifiutato la connessione (password errata?)");
            return;
        }
        await receive;
    }

    private static string AuthString(string password, string salt, string challenge)
    {
        var secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        while (ws.State == WebSocketState.Open)
        {
            var msg = await ReceiveAsync(ws, ct);
            if (msg is null) return;
            var d = msg["d"] as JsonObject;
            switch (J.Int(msg["op"]))
            {
                case 2: // Identified
                    _identified = true;
                    _lastError = null;
                    _live.Publish("obs.conn", LiveService.V(L.T("Connesso"), state: true));
                    _ = InitialStateAsync();
                    break;
                case 5: OnEvent(J.Str(d?["eventType"]), d?["eventData"] as JsonObject); break;
                case 7:
                    if (J.Str(d?["requestId"]) is { } id && _pending.TryRemove(id, out var tcs)) tcs.TrySetResult(d!);
                    break;
            }
        }
    }

    private static async Task<JsonObject?> ReceiveAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        WebSocketReceiveResult r;
        do
        {
            r = await ws.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, r.Count);
            if (ms.Length > 4 * 1024 * 1024) throw new IOException(L.T("Messaggio OBS troppo grande"));
        } while (!r.EndOfMessage);
        return JsonNode.Parse(ms.ToArray()) as JsonObject;
    }

    private async Task SendAsync(JsonObject msg)
    {
        var ws = _ws ?? throw new ActionException(L.T("OBS non connesso"));
        var bytes = Encoding.UTF8.GetBytes(msg.ToJsonString());
        await _sendGate.WaitAsync();
        try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None); }
        finally { _sendGate.Release(); }
    }

    /// <summary>Sends a request and returns responseData (throws a user-facing error on failure).</summary>
    public async Task<JsonObject?> RequestAsync(string type, JsonObject? data = null)
    {
        if (!_identified)
            throw new ActionException(_lastError is null ? L.T("OBS non connesso") : L.F("OBS non connesso: {0}", _lastError));
        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var d = new JsonObject { ["requestType"] = type, ["requestId"] = id };
        if (data is not null) d["requestData"] = data;
        await SendAsync(new JsonObject { ["op"] = 6, ["d"] = d });
        var res = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var status = res["requestStatus"];
        if (J.Bool(status?["result"]) != true)
            throw new ActionException($"OBS: {J.Str(status?["comment"]) ?? L.F("errore {0}", J.Int(status?["code"]))}");
        return res["responseData"] as JsonObject;
    }

    private async Task InitialStateAsync()
    {
        try
        {
            var stream = await RequestAsync("GetStreamStatus");
            PublishStream(J.Bool(stream?["outputActive"]) ?? false);
            var rec = await RequestAsync("GetRecordStatus");
            PublishRecord(J.Bool(rec?["outputActive"]) ?? false, J.Bool(rec?["outputPaused"]) ?? false);
            var scene = await RequestAsync("GetCurrentProgramScene");
            PublishScene(J.Str(scene?["currentProgramSceneName"]) ?? J.Str(scene?["sceneName"]) ?? "");
            await RefreshMutesAsync();
        }
        catch (Exception ex) { Log.Info($"OBS initial state failed: {ex.Message}"); }
    }

    private async Task RefreshMutesAsync()
    {
        foreach (var src in _live.Referenced.Where(s => s.StartsWith("obs.mute:", StringComparison.Ordinal)))
        {
            try
            {
                var r = await RequestAsync("GetInputMute", new JsonObject { ["inputName"] = src[9..] });
                PublishMute(src[9..], J.Bool(r?["inputMuted"]) ?? false);
            }
            catch { _live.Publish(src, LiveService.V("—", state: false)); }
        }
    }

    private void OnEvent(string? type, JsonObject? data)
    {
        switch (type)
        {
            case "StreamStateChanged": PublishStream(J.Bool(data?["outputActive"]) ?? false); break;
            case "RecordStateChanged":
                var state = J.Str(data?["outputState"]);
                PublishRecord(J.Bool(data?["outputActive"]) ?? false, state == "OBS_WEBSOCKET_OUTPUT_PAUSED");
                break;
            case "CurrentProgramSceneChanged": PublishScene(J.Str(data?["sceneName"]) ?? ""); break;
            case "InputMuteStateChanged":
                if (J.Str(data?["inputName"]) is { } input) PublishMute(input, J.Bool(data?["inputMuted"]) ?? false);
                break;
        }
    }

    private void PublishStream(bool on) => _live.Publish("obs.stream", LiveService.V(on ? L.T("IN DIRETTA") : "Offline", state: on));
    private void PublishRecord(bool on, bool paused) =>
        _live.Publish("obs.rec", LiveService.V(paused ? L.T("In pausa") : on ? "REC" : L.T("Fermo"), state: on && !paused));
    private void PublishScene(string name) => _live.Publish("obs.scene", LiveService.V(name));
    private void PublishMute(string input, bool muted) =>
        _live.Publish($"obs.mute:{input}", LiveService.V(muted ? L.T("Muto") : L.T("Attivo"), state: muted));

    /// <summary>Scene and audio input names for the editor.</summary>
    public async Task<JsonObject> InfoAsync()
    {
        var o = PublicSettings();
        o["connected"] = _identified;
        o["error"] = _lastError;
        if (!_identified) return o;
        try
        {
            var scenes = await RequestAsync("GetSceneList");
            o["scenes"] = new JsonArray((scenes?["scenes"] as JsonArray ?? [])
                .Select(s => (JsonNode?)JsonValue.Create(J.Str(s?["sceneName"]))).Reverse().ToArray());
            var inputs = await RequestAsync("GetInputList");
            o["inputs"] = new JsonArray((inputs?["inputs"] as JsonArray ?? [])
                .Select(s => (JsonNode?)JsonValue.Create(J.Str(s?["inputName"]))).ToArray());
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
