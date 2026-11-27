using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using PadNook.Actions;
using PadNook.Config;
using PadNook.Core;

namespace PadNook.Live;

/// <summary>
/// Discord desktop app through its local RPC: the named pipe "discord-ipc-N", frames of
/// [opcode int32 LE][length int32 LE][JSON]. Voice control needs the rpc.voice.* scopes, which Discord
/// only grants to the owner (and testers) of an application: the user creates their own app on the
/// Developer Portal and enters its Client ID and Client Secret. Login = AUTHORIZE (popup in Discord) →
/// code exchanged for tokens → AUTHENTICATE. Tokens are stored encrypted in the SecretStore.
/// Live sources: discord.conn, discord.mute, discord.deaf, discord.invol, discord.outvol, discord.channel, discord.ptt.
/// </summary>
public sealed class DiscordClient : IDisposable
{
    public const string RedirectUri = "http://localhost";
    private const string SecretName = "__discord.secret";
    private const string RefreshName = "__discord.refresh";
    private const string AccessName = "__discord.access";
    private static readonly string[] Scopes = ["rpc", "rpc.voice.read", "rpc.voice.write", "identify"];

    private static readonly bool Dev = Environment.GetEnvironmentVariable("PADNOOK_DEV") == "1";
    private static readonly string PipePrefix =
        Dev && Environment.GetEnvironmentVariable("PADNOOK_DEV_DISCORD_PIPE") is { Length: > 0 } p ? p : "discord-ipc-";
    private static readonly string ApiBase =
        Dev && Environment.GetEnvironmentVariable("PADNOOK_DEV_DISCORD_API") is { Length: > 0 } a ? a.TrimEnd('/') : "https://discord.com/api";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private const int OpHandshake = 0, OpFrame = 1, OpClose = 2, OpPing = 3, OpPong = 4;

    private readonly ConfigStore _config;
    private readonly SecretStore _secrets;
    private readonly LiveService _live;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private Stream? _pipe;
    private CancellationTokenSource? _connCts;
    private TaskCompletionSource<bool>? _ready;
    private volatile bool _connected;
    private volatile bool _authorized;
    private volatile bool _loginPending;
    private string? _user;
    private string? _lastError;
    private JsonObject? _voice;

    public DiscordClient(ConfigStore config, SecretStore secrets, LiveService live)
    {
        _config = config;
        _secrets = secrets;
        _live = live;
    }

    // ---- Settings -----------------------------------------------------------------------------

    private (bool Enabled, string ClientId) Settings()
    {
        var o = _config.SettingsSnapshot()["discord"];
        return (J.Bool(o?["enabled"]) ?? true, (J.Str(o?["clientId"]) ?? "").Trim());
    }

    private long ExpiresAt => J.Long(_config.SettingsSnapshot()["discord"]?["expiresAt"]) ?? 0;

    public JsonObject Info()
    {
        var (enabled, clientId) = Settings();
        return new JsonObject
        {
            ["enabled"] = enabled,
            ["clientId"] = clientId,
            ["hasSecret"] = _secrets.Has(SecretName),
            ["hasToken"] = _secrets.Has(RefreshName),
            ["connected"] = _connected,
            ["authorized"] = _authorized,
            ["loginPending"] = _loginPending,
            ["user"] = _user,
            ["error"] = _lastError,
            ["redirectUri"] = RedirectUri,
        };
    }

    public void UpdateSettings(bool enabled, string clientId, string? secret)
    {
        clientId = clientId.Trim();
        if (clientId.Length > 0 && !clientId.All(char.IsAsciiDigit)) throw new ArgumentException(L.T("Il Client ID è fatto solo di cifre"));
        var old = Settings().ClientId;
        _config.UpdateSettings(s =>
        {
            var o = s["discord"] as JsonObject ?? new JsonObject();
            o["enabled"] = enabled;
            o["clientId"] = clientId;
            s["discord"] = o;
        });
        if (!string.IsNullOrWhiteSpace(secret)) _secrets.Set(SecretName, secret.Trim());
        // Another application: its tokens are not valid any more.
        if (old != clientId) ClearTokens();
        _connCts?.Cancel();
    }

    public void Logout()
    {
        ClearTokens();
        _connCts?.Cancel();
    }

    private void ClearTokens()
    {
        _secrets.Delete(RefreshName);
        _secrets.Delete(AccessName);
        _user = null;
    }

    // ---- Connection loop ----------------------------------------------------------------------

    public void Start() => _ = Task.Run(() => LoopAsync(_cts.Token));

    private async Task LoopAsync(CancellationToken ct)
    {
        Disconnected();
        while (!ct.IsCancellationRequested)
        {
            var (enabled, clientId) = Settings();
            if (enabled && clientId.Length > 0)
            {
                _connCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                try { await RunAsync(clientId, _connCts.Token); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (OperationCanceledException) { }
                catch (TimeoutException) { _lastError = L.T("Discord non è aperto"); }
                catch (IOException) { _lastError ??= L.T("Discord non è aperto"); }
                catch (ActionException ex) { _lastError = ex.Message; }
                catch (Exception ex)
                {
                    _lastError = ex.Message;
                    Log.Error("Discord connection failed", ex);
                }
                Disconnected();
            }
            else _lastError = null;
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void Disconnected()
    {
        _connected = false;
        _authorized = false;
        _pipe = null;
        _voice = null;
        foreach (var p in _pending.Values) p.TrySetCanceled();
        _pending.Clear();
        _live.Publish("discord.conn", LiveService.V(L.T("Non connesso"), state: false));
        foreach (var k in new[] { "discord.mute", "discord.deaf", "discord.ptt" })
            _live.Publish(k, LiveService.V(L.T("Discord chiuso"), state: false));
        _live.Publish("discord.channel", LiveService.V("—", state: false));
    }

    private static async Task<NamedPipeClientStream> OpenPipeAsync(CancellationToken ct)
    {
        // Discord listens on the first free name, discord-ipc-0 … discord-ipc-9.
        for (var i = 0; i < 10; i++)
        {
            var pipe = new NamedPipeClientStream(".", PipePrefix + i, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(150, ct);
                return pipe;
            }
            catch (TimeoutException) { await pipe.DisposeAsync(); }
            catch (IOException) { await pipe.DisposeAsync(); }
        }
        throw new TimeoutException();
    }

    private async Task RunAsync(string clientId, CancellationToken ct)
    {
        await using var pipe = await OpenPipeAsync(ct);
        _pipe = pipe;
        _ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await WriteFrameAsync(OpHandshake, new JsonObject { ["v"] = 1, ["client_id"] = clientId }, ct);
        var receive = Task.Run(() => ReceiveLoopAsync(pipe, ct), ct);

        var first = await Task.WhenAny(_ready.Task, receive, Task.Delay(TimeSpan.FromSeconds(10), ct));
        if (first != _ready.Task)
        {
            if (first == receive) await receive; // surfaces the close reason
            throw new ActionException(L.T("Discord non ha risposto alla connessione"));
        }
        _connected = true;
        _lastError = null;
        _live.Publish("discord.conn", LiveService.V(L.T("Accesso richiesto"), state: false));

        if (_secrets.Has(RefreshName))
        {
            try { await AuthenticateAsync(clientId, ct); }
            catch (ActionException ex)
            {
                _lastError = ex.Message;
                Log.Info($"Discord authentication failed: {ex.Message}");
            }
        }
        await receive;
    }

    private async Task ReceiveLoopAsync(Stream pipe, CancellationToken ct)
    {
        var header = new byte[8];
        while (!ct.IsCancellationRequested)
        {
            await pipe.ReadExactlyAsync(header, ct);
            var op = BinaryPrimitives.ReadInt32LittleEndian(header);
            var len = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
            if (len is < 0 or > 8 * 1024 * 1024) throw new IOException(L.T("Frame Discord non valido"));
            var body = new byte[len];
            await pipe.ReadExactlyAsync(body, ct);
            JsonObject? msg;
            try { msg = JsonNode.Parse(body) as JsonObject; }
            catch { continue; }
            switch (op)
            {
                case OpFrame when msg is not null:
                    try { OnFrame(msg); }
                    catch (Exception ex) { Log.Error("Discord frame failed", ex); }
                    break;
                case OpPing:
                    await WriteFrameAsync(OpPong, msg ?? new JsonObject(), ct);
                    break;
                case OpClose:
                    // e.g. code 4000 "Invalid Client ID".
                    var reason = J.Str(msg?["message"]);
                    throw new ActionException(reason is { Length: > 0 } ? L.F("Discord ha chiuso la connessione: {0}", reason) : L.T("Discord ha chiuso la connessione"));
            }
        }
    }

    private void OnFrame(JsonObject msg)
    {
        var cmd = J.Str(msg["cmd"]);
        var evt = J.Str(msg["evt"]);
        var nonce = J.Str(msg["nonce"]);
        if (cmd == "DISPATCH")
        {
            switch (evt)
            {
                case "READY": _ready?.TrySetResult(true); break;
                case "VOICE_SETTINGS_UPDATE": PublishVoice(msg["data"] as JsonObject); break;
                case "VOICE_CHANNEL_SELECT": _ = RefreshChannelAsync(); break;
            }
            return;
        }
        if (nonce is null || !_pending.TryRemove(nonce, out var tcs)) return;
        if (evt == "ERROR")
            tcs.TrySetException(new ActionException(J.Str(msg["data"]?["message"]) ?? L.T("Errore di Discord")));
        else tcs.TrySetResult(msg["data"]);
    }

    private async Task WriteFrameAsync(int op, JsonObject payload, CancellationToken ct)
    {
        var pipe = _pipe ?? throw new ActionException(NotConnectedMessage());
        var json = Encoding.UTF8.GetBytes(payload.ToJsonString());
        var frame = new byte[8 + json.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, op);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), json.Length);
        json.CopyTo(frame, 8);
        await _writeGate.WaitAsync(ct);
        try
        {
            await pipe.WriteAsync(frame, ct);
            await pipe.FlushAsync(ct);
        }
        finally { _writeGate.Release(); }
    }

    private string NotConnectedMessage() => _lastError is { } e ? L.F("Discord non connesso: {0}", e) : L.T("Discord non connesso");

    /// <summary>Sends an RPC command and waits for its answer.</summary>
    private async Task<JsonNode?> CallAsync(string cmd, JsonObject? args = null, string? evt = null, TimeSpan? timeout = null)
    {
        if (!_connected) throw new ActionException(NotConnectedMessage());
        var nonce = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[nonce] = tcs;
        var payload = new JsonObject { ["cmd"] = cmd, ["args"] = args ?? new JsonObject(), ["nonce"] = nonce };
        if (evt is not null) payload["evt"] = evt;
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(8));
        try
        {
            await WriteFrameAsync(OpFrame, payload, cts.Token);
            using (cts.Token.Register(() => tcs.TrySetException(new ActionException(L.T("Discord non risponde")))))
                return await tcs.Task;
        }
        catch (TaskCanceledException) { throw new ActionException(NotConnectedMessage()); }
        finally { _pending.TryRemove(nonce, out _); }
    }

    private async Task<JsonNode?> AuthorizedCallAsync(string cmd, JsonObject? args = null)
    {
        if (!_connected) throw new ActionException(NotConnectedMessage());
        if (!_authorized) throw new ActionException(L.T("Discord: accesso non ancora fatto (editor → Servizi → Discord → «Accedi con Discord»)"));
        return await CallAsync(cmd, args);
    }

    // ---- OAuth --------------------------------------------------------------------------------

    /// <summary>Editor «Accedi con Discord»: Discord shows its authorization popup, then tokens are saved.</summary>
    public async Task LoginAsync()
    {
        var (_, clientId) = Settings();
        if (clientId.Length == 0) throw new ActionException(L.T("Inserisci prima il Client ID"));
        if (!_secrets.Has(SecretName)) throw new ActionException(L.T("Inserisci prima il Client Secret"));
        if (!_connected) throw new ActionException(NotConnectedMessage());
        if (_loginPending) throw new ActionException(L.T("Accesso già in corso: conferma la finestra in Discord"));
        _loginPending = true;
        try
        {
            var data = await CallAsync("AUTHORIZE", new JsonObject
            {
                ["client_id"] = clientId,
                ["scopes"] = new JsonArray(Scopes.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
            }, timeout: TimeSpan.FromMinutes(2));
            var code = J.Str(data?["code"]) ?? throw new ActionException(L.T("Discord non ha restituito il codice di accesso"));
            await TokenRequestAsync(clientId, new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = RedirectUri,
            });
            await AuthenticateAsync(clientId, _connCts?.Token ?? CancellationToken.None);
        }
        catch (ActionException ex) when (ex.Message.Contains("cancel", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("denied", StringComparison.OrdinalIgnoreCase))
        {
            throw new ActionException(L.T("Accesso annullato in Discord"));
        }
        finally { _loginPending = false; }
    }

    private async Task TokenRequestAsync(string clientId, Dictionary<string, string> form)
    {
        var secret = _secrets.Get(SecretName) ?? throw new ActionException(L.T("Client Secret mancante o non leggibile: inseriscilo di nuovo"));
        form["client_id"] = clientId;
        form["client_secret"] = secret;
        HttpResponseMessage res;
        try { res = await Http.PostAsync($"{ApiBase}/oauth2/token", new FormUrlEncodedContent(form)); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new ActionException(L.T("Impossibile contattare discord.com (connessione a Internet?)"));
        }
        using (res)
        {
            JsonObject? body = null;
            try { body = await res.Content.ReadFromJsonAsync<JsonObject>(); }
            catch { /* not JSON */ }
            if (!res.IsSuccessStatusCode)
            {
                var err = J.Str(body?["error"]);
                var desc = J.Str(body?["error_description"]);
                throw new ActionException(err switch
                {
                    "invalid_client" => L.T("Client ID o Client Secret non validi"),
                    "invalid_grant" when form["grant_type"] == "refresh_token" => L.T("Accesso scaduto: premi di nuovo «Accedi con Discord»"),
                    "invalid_grant" => L.F("Codice rifiutato da Discord: controlla di aver aggiunto il redirect {0} nel Developer Portal (OAuth2 → Redirects)", RedirectUri),
                    _ => L.F("Discord ha rifiutato l'accesso ({0})", $"{(int)res.StatusCode}{((desc ?? err) is { } d ? ": " + d : "")}"),
                });
            }
            var access = J.Str(body?["access_token"]) ?? throw new ActionException(L.T("Risposta di Discord senza token"));
            _secrets.Set(AccessName, access);
            if (J.Str(body?["refresh_token"]) is { Length: > 0 } refresh) _secrets.Set(RefreshName, refresh);
            var expires = DateTimeOffset.UtcNow.AddSeconds(J.Int(body?["expires_in"]) ?? 604800).ToUnixTimeSeconds();
            _config.UpdateSettings(s =>
            {
                var o = s["discord"] as JsonObject ?? new JsonObject();
                o["expiresAt"] = expires;
                s["discord"] = o;
            });
        }
    }

    private async Task AuthenticateAsync(string clientId, CancellationToken ct)
    {
        // Refresh a day before expiry (access tokens last 7 days).
        if (_secrets.Get(AccessName) is null || DateTimeOffset.UtcNow.ToUnixTimeSeconds() > ExpiresAt - 86400)
        {
            var refresh = _secrets.Get(RefreshName) ?? throw new ActionException(L.T("Accesso a Discord da rifare"));
            await TokenRequestAsync(clientId, new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh });
        }
        JsonNode? data;
        try { data = await CallAsync("AUTHENTICATE", new JsonObject { ["access_token"] = _secrets.Get(AccessName) }); }
        catch (ActionException)
        {
            // Revoked or stale access token: one refresh, then retry.
            var refresh = _secrets.Get(RefreshName) ?? throw new ActionException(L.T("Accesso a Discord da rifare"));
            await TokenRequestAsync(clientId, new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh });
            data = await CallAsync("AUTHENTICATE", new JsonObject { ["access_token"] = _secrets.Get(AccessName) });
        }
        var u = data?["user"];
        _user = J.Str(u?["global_name"]) ?? J.Str(u?["username"]);
        _authorized = true;
        _lastError = null;
        _live.Publish("discord.conn", LiveService.V(L.T("Connesso"), state: true, sub: _user));

        await CallAsync("SUBSCRIBE", evt: "VOICE_SETTINGS_UPDATE");
        await CallAsync("SUBSCRIBE", evt: "VOICE_CHANNEL_SELECT");
        PublishVoice(await CallAsync("GET_VOICE_SETTINGS") as JsonObject);
        await RefreshChannelAsync();
    }

    // ---- Live state ---------------------------------------------------------------------------

    private void PublishVoice(JsonObject? v)
    {
        if (v is null) return;
        _voice = v;
        var mute = J.Bool(v["mute"]) ?? false;
        var deaf = J.Bool(v["deaf"]) ?? false;
        var ptt = J.Str(v["mode"]?["type"]) == "PUSH_TO_TALK";
        var inVol = Math.Round(J.Num(v["input"]?["volume"]) ?? 100);
        var outVol = Math.Round(J.Num(v["output"]?["volume"]) ?? 100);
        _live.PublishMany([
            ("discord.mute", LiveService.V(mute ? L.T("Muto") : L.T("Mic attivo"), state: mute)),
            ("discord.deaf", LiveService.V(deaf ? L.T("Silenziato") : L.T("Audio attivo"), state: deaf)),
            ("discord.ptt", LiveService.V(ptt ? "Push-to-talk" : L.T("Attività vocale"), state: ptt)),
            ("discord.invol", LiveService.V($"{inVol}%", inVol)),
            ("discord.outvol", LiveService.V($"{outVol}%", outVol)),
        ]);
    }

    private async Task RefreshChannelAsync()
    {
        try
        {
            var ch = await CallAsync("GET_SELECTED_VOICE_CHANNEL");
            var name = ch is JsonObject o ? J.Str(o["name"]) : null;
            _live.Publish("discord.channel", name is null ? LiveService.V(L.T("Nessun canale"), state: false) : LiveService.V(name, state: true));
        }
        catch (ActionException) { /* not critical */ }
    }

    // ---- Commands -----------------------------------------------------------------------------

    private async Task<JsonObject> VoiceAsync() =>
        _voice ?? (await AuthorizedCallAsync("GET_VOICE_SETTINGS") as JsonObject) ?? throw new ActionException(L.T("Impostazioni vocali di Discord non disponibili"));

    private async Task SetVoiceAsync(JsonObject args) => PublishVoice(await AuthorizedCallAsync("SET_VOICE_SETTINGS", args) as JsonObject);

    public async Task RunAsync(JsonObject a)
    {
        switch (J.Str(a["command"]))
        {
            case "toggleMute": await SetVoiceAsync(new JsonObject { ["mute"] = !(J.Bool((await VoiceAsync())["mute"]) ?? false) }); break;
            case "mute": await SetVoiceAsync(new JsonObject { ["mute"] = true }); break;
            case "unmute": await SetVoiceAsync(new JsonObject { ["mute"] = false }); break;
            case "toggleDeaf": await SetVoiceAsync(new JsonObject { ["deaf"] = !(J.Bool((await VoiceAsync())["deaf"]) ?? false) }); break;
            case "deaf": await SetVoiceAsync(new JsonObject { ["deaf"] = true }); break;
            case "undeaf": await SetVoiceAsync(new JsonObject { ["deaf"] = false }); break;
            case "togglePtt":
            {
                var ptt = J.Str((await VoiceAsync())["mode"]?["type"]) == "PUSH_TO_TALK";
                await SetVoiceAsync(new JsonObject { ["mode"] = new JsonObject { ["type"] = ptt ? "VOICE_ACTIVITY" : "PUSH_TO_TALK" } });
                break;
            }
            case "inputVolume":
                await SetVoiceAsync(new JsonObject { ["input"] = new JsonObject { ["volume"] = Math.Clamp(J.Num(a["level"]) ?? 100, 0, 100) } });
                break;
            case "outputVolume":
                await SetVoiceAsync(new JsonObject { ["output"] = new JsonObject { ["volume"] = Math.Clamp(J.Num(a["level"]) ?? 100, 0, 200) } });
                break;
            case "join":
            {
                var id = (J.Str(a["channelId"]) ?? "").Trim();
                if (id.Length == 0 || !id.All(char.IsAsciiDigit)) throw new ActionException(L.T("ID del canale vocale mancante o non valido"));
                await AuthorizedCallAsync("SELECT_VOICE_CHANNEL", new JsonObject { ["channel_id"] = id, ["force"] = true });
                break;
            }
            case "leave":
                await AuthorizedCallAsync("SELECT_VOICE_CHANNEL", new JsonObject { ["channel_id"] = null });
                break;
            default: throw new ActionException(L.T("Comando Discord non valido"));
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
