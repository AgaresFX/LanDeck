using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using PadNook.Actions;
using PadNook.Config;
using PadNook.Core;

namespace PadNook.Live;

/// <summary>
/// Twitch: login with the Device Code Flow as a public client (no secret in the exe), tokens in the
/// DPAPI store, hourly /validate, Helix calls with refresh on 401, live sources twitch.* and the
/// EventSub WebSocket (<see cref="TwitchEventSub"/>).
/// </summary>
public sealed class TwitchService : IDisposable
{
    /// <summary>Client ID of the PadNook app registered on dev.twitch.tv (public client). Overridable in the editor.</summary>
    public const string DefaultClientId = "";

    public static readonly string[] Scopes =
    [
        "channel:manage:broadcast", "clips:edit", "channel:edit:commercial", "channel:manage:raids",
        "channel:manage:polls", "channel:manage:predictions", "user:write:chat", "user:read:chat",
        "moderator:manage:announcements", "moderator:manage:shoutouts", "moderator:manage:chat_settings",
        "moderator:read:followers", "channel:read:subscriptions", "bits:read", "channel:read:redemptions",
    ];

    // Development only (PADNOOK_DEV=1): a local mock of Twitch for automated tests.
    private static string? DevUrl(string name) =>
        Environment.GetEnvironmentVariable("PADNOOK_DEV") == "1" && Environment.GetEnvironmentVariable(name) is { Length: > 0 } u ? u.TrimEnd('/') : null;
    private static readonly string IdBase = DevUrl("PADNOOK_DEV_TWITCH_ID") ?? "https://id.twitch.tv";
    private static readonly string ApiBase = DevUrl("PADNOOK_DEV_TWITCH_API") ?? "https://api.twitch.tv";
    public static readonly string EventSubUrl = DevUrl("PADNOOK_DEV_TWITCH_WS") ?? "wss://eventsub.wss.twitch.tv/ws?keepalive_timeout_seconds=30";

    private const string AccessSecret = "__twitch.access";
    private const string RefreshSecret = "__twitch.refresh";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly ConfigStore _config;
    private readonly SecretStore _secrets;
    private readonly LiveService _live;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private CancellationTokenSource? _loginCts;
    private JsonObject? _login; // pending device login: userCode, uri, expiresAt
    private string? _error;
    private string[] _grantedScopes = [];
    private DateTimeOffset _nextValidate;
    private DateTimeOffset _nextPoll;

    public TwitchEventSub EventSub { get; }

    /// <summary>Raised when login state or settings change (EventSub reconnects).</summary>
    public event Action? AuthChanged;

    public TwitchService(ConfigStore config, SecretStore secrets, LiveService live)
    {
        _config = config;
        _secrets = secrets;
        _live = live;
        EventSub = new TwitchEventSub(this, live);
    }

    // ---- Settings -----------------------------------------------------------------------------

    private JsonObject Settings() => _config.SettingsSnapshot()["twitch"] as JsonObject ?? new JsonObject();

    public void UpdateSettings(Action<JsonObject> mutate)
    {
        _config.UpdateSettings(s =>
        {
            var o = s["twitch"] as JsonObject ?? new JsonObject();
            mutate(o);
            s["twitch"] = o;
        });
    }

    public string ClientId => J.Str(Settings()["clientId"]) is { Length: > 0 } id ? id.Trim() : DefaultClientId;

    public bool LoggedIn => _secrets.Has(RefreshSecret) && UserId is not null;
    public string? UserId => J.Str(Settings()["user"]?["id"]);
    public string? UserLogin => J.Str(Settings()["user"]?["login"]);
    public string? UserName => J.Str(Settings()["user"]?["name"]);
    public JsonArray Rules => Settings()["rules"] as JsonArray ?? [];

    public JsonObject Info()
    {
        var o = new JsonObject
        {
            ["clientId"] = J.Str(Settings()["clientId"]) ?? "",
            ["hasDefaultClientId"] = DefaultClientId.Length > 0,
            ["connected"] = LoggedIn,
            ["login"] = UserLogin,
            ["name"] = UserName,
            ["error"] = _error,
            ["pending"] = _login?.DeepClone(),
            ["missingScopes"] = new JsonArray(LoggedIn
                ? Scopes.Where(s => !_grantedScopes.Contains(s)).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()
                : []),
            ["eventsub"] = EventSub.Status(),
            ["rules"] = Rules.DeepClone(),
        };
        return o;
    }

    public void Start() => _ = Task.Run(() => LoopAsync(_cts.Token));

    // ---- Device Code Flow ---------------------------------------------------------------------

    public async Task<JsonObject> StartLoginAsync()
    {
        var clientId = ClientId;
        if (clientId.Length == 0)
            throw new ActionException(L.T("Manca il Client ID: registra l'app su dev.twitch.tv/console e inseriscilo qui sotto"));
        _loginCts?.Cancel();
        var res = await PostFormAsync($"{IdBase}/oauth2/device", new()
        {
            ["client_id"] = clientId,
            ["scopes"] = string.Join(' ', Scopes),
        });
        var deviceCode = J.Str(res["device_code"]) ?? throw new ActionException(L.T("Twitch non ha restituito un codice"));
        var interval = Math.Max(1, J.Int(res["interval"]) ?? 5);
        var expires = DateTimeOffset.Now.AddSeconds(J.Int(res["expires_in"]) ?? 1800);
        var uri = J.Str(res["verification_uri"]) ?? "https://www.twitch.tv/activate";
        _login = new JsonObject
        {
            ["userCode"] = J.Str(res["user_code"]),
            ["uri"] = uri,
            ["expiresAt"] = expires.ToUnixTimeMilliseconds(),
        };
        _error = null;
        if (DevUrl("PADNOOK_DEV_TWITCH_ID") is null)
            try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { Log.Info($"Unable to open the browser: {ex.Message}"); }

        var cts = _loginCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _ = Task.Run(() => PollLoginAsync(clientId, deviceCode, interval, expires, cts.Token));
        return (JsonObject)_login.DeepClone();
    }

    public void CancelLogin()
    {
        _loginCts?.Cancel();
        _login = null;
    }

    private async Task PollLoginAsync(string clientId, string deviceCode, int interval, DateTimeOffset expires, CancellationToken ct)
    {
        try
        {
            while (DateTimeOffset.Now < expires)
            {
                await Task.Delay(TimeSpan.FromSeconds(interval), ct);
                var (status, body) = await PostFormRawAsync($"{IdBase}/oauth2/token", new()
                {
                    ["client_id"] = clientId,
                    ["scopes"] = string.Join(' ', Scopes),
                    ["device_code"] = deviceCode,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                }, ct);
                if (status == HttpStatusCode.OK)
                {
                    await StoreTokensAsync(body);
                    _login = null;
                    Log.Info($"Twitch: logged in as {UserLogin}");
                    Kick();
                    AuthChanged?.Invoke();
                    return;
                }
                var msg = J.Str(body["message"]) ?? "";
                if (msg.Contains("authorization_pending", StringComparison.OrdinalIgnoreCase)) continue;
                if (msg.Contains("slow_down", StringComparison.OrdinalIgnoreCase)) { interval += 5; continue; }
                _error = msg.Contains("invalid device code", StringComparison.OrdinalIgnoreCase)
                    ? L.T("Accesso non completato o rifiutato: riprova") : L.F("Accesso non riuscito: {0}", msg);
                _login = null;
                return;
            }
            _error = L.T("Il codice è scaduto: premi di nuovo «Accedi con Twitch»");
            _login = null;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _error = L.F("Accesso non riuscito: {0}", ex.Message);
            _login = null;
        }
    }

    private async Task StoreTokensAsync(JsonObject tokenResponse)
    {
        var access = J.Str(tokenResponse["access_token"]) ?? throw new IOException("access_token mancante");
        var refresh = J.Str(tokenResponse["refresh_token"]) ?? throw new IOException("refresh_token mancante");
        _secrets.Set(AccessSecret, access);
        _secrets.Set(RefreshSecret, refresh);
        await ValidateAsync(access);
    }

    public void Logout()
    {
        _secrets.Delete(AccessSecret);
        _secrets.Delete(RefreshSecret);
        UpdateSettings(o => o.Remove("user"));
        _grantedScopes = [];
        _error = null;
        PublishDisconnected();
        AuthChanged?.Invoke();
    }

    /// <summary>GET /validate: required at startup and every hour. Returns false if the token is invalid.</summary>
    private async Task<bool> ValidateAsync(string access)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{IdBase}/oauth2/validate");
        req.Headers.Authorization = new AuthenticationHeaderValue("OAuth", access);
        using var res = await Http.SendAsync(req);
        if (res.StatusCode == HttpStatusCode.Unauthorized) return false;
        res.EnsureSuccessStatusCode();
        var o = JsonNode.Parse(await res.Content.ReadAsStringAsync()) as JsonObject ?? new JsonObject();
        _grantedScopes = (o["scopes"] as JsonArray ?? []).Select(s => J.Str(s) ?? "").ToArray();
        var id = J.Str(o["user_id"]);
        var login = J.Str(o["login"]);
        if (id is not null && (id != UserId || login != UserLogin || UserName is null))
        {
            var name = login;
            try
            {
                var u = await HelixAsync(HttpMethod.Get, "users", $"id={id}", tokenOverride: access);
                name = J.Str(u?["data"]?[0]?["display_name"]) ?? login;
            }
            catch { /* display name is cosmetic */ }
            UpdateSettings(s => s["user"] = new JsonObject { ["id"] = id, ["login"] = login, ["name"] = name });
        }
        _nextValidate = DateTimeOffset.Now.AddHours(1);
        _error = null;
        _live.Publish("twitch.conn", LiveService.V(UserName ?? login ?? L.T("Connesso"), state: true));
        return true;
    }

    /// <summary>Refresh tokens are single-use: the new one is stored before anything else.</summary>
    private async Task<string?> RefreshAsync(string usedAccess)
    {
        await _refreshGate.WaitAsync();
        try
        {
            var current = _secrets.Get(AccessSecret);
            if (current is not null && current != usedAccess) return current; // another call refreshed already
            var refresh = _secrets.Get(RefreshSecret);
            if (refresh is null) return null;
            var (status, body) = await PostFormRawAsync($"{IdBase}/oauth2/token", new()
            {
                ["client_id"] = ClientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refresh,
            }, CancellationToken.None);
            if (status != HttpStatusCode.OK)
            {
                if (status is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
                {
                    // Expired (public-client refresh tokens last 30 days) or revoked: log in again.
                    _secrets.Delete(AccessSecret);
                    _secrets.Delete(RefreshSecret);
                    _error = L.T("Accesso a Twitch scaduto: premi «Accedi con Twitch»");
                    PublishDisconnected();
                    AuthChanged?.Invoke();
                }
                return null;
            }
            var access = J.Str(body["access_token"]);
            var newRefresh = J.Str(body["refresh_token"]);
            if (newRefresh is not null) _secrets.Set(RefreshSecret, newRefresh);
            if (access is not null) _secrets.Set(AccessSecret, access);
            return access;
        }
        finally { _refreshGate.Release(); }
    }

    /// <summary>A valid user access token (refreshed if needed) for EventSub.</summary>
    public async Task<string?> AccessTokenAsync(bool forceRefresh = false)
    {
        var access = _secrets.Get(AccessSecret);
        if (access is null || forceRefresh) access = await RefreshAsync(access ?? "");
        return access;
    }

    // ---- Helix --------------------------------------------------------------------------------

    /// <summary>Calls https://api.twitch.tv/helix/PATH. Refreshes the token once on 401.</summary>
    public async Task<JsonObject?> HelixAsync(HttpMethod method, string path, string? query = null, JsonObject? body = null, string? tokenOverride = null)
    {
        var token = tokenOverride ?? _secrets.Get(AccessSecret);
        if (token is null)
        {
            if (!_secrets.Has(RefreshSecret)) throw new ActionException(L.T("Twitch non collegato: accedi dal pannello del profilo → Twitch"));
            token = await RefreshAsync("") ?? throw new ActionException(L.T("Twitch non collegato: accedi di nuovo"));
        }
        for (var attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(method, $"{ApiBase}/helix/{path}{(string.IsNullOrEmpty(query) ? "" : "?" + query)}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Add("Client-Id", ClientId);
            if (body is not null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            HttpResponseMessage res;
            try { res = await Http.SendAsync(req); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                throw new ActionException(L.T("Twitch non risponde: il PC è connesso a Internet?"));
            }
            using (res)
            {
                var text = await res.Content.ReadAsStringAsync();
                if (res.StatusCode == HttpStatusCode.Unauthorized && attempt == 0 && tokenOverride is null)
                {
                    token = await RefreshAsync(token) ?? throw new ActionException(L.T("Accesso a Twitch scaduto: accedi di nuovo"));
                    continue;
                }
                JsonObject? json = null;
                try { json = text.Length > 0 ? JsonNode.Parse(text) as JsonObject : null; } catch { }
                if (res.IsSuccessStatusCode) return json;
                throw new ActionException(ErrorText(res.StatusCode, J.Str(json?["message"])));
            }
        }
    }

    private static string ErrorText(HttpStatusCode code, string? message) => (int)code switch
    {
        403 => L.F("Twitch: permesso mancante ({0}). Accedi di nuovo per concedere i permessi", message),
        429 => L.F("Twitch: troppe richieste, riprova tra poco{0}", message is null ? "" : $" ({message})"),
        _ => $"Twitch: {message ?? L.F("errore {0}", (int)code)}",
    };

    public string RequireUser() => UserId ?? throw new ActionException(L.T("Twitch non collegato: accedi dal pannello del profilo → Twitch"));

    /// <summary>User id from a channel name (login), for raid and shoutout.</summary>
    public async Task<(string Id, string Name)> UserByLoginAsync(string login)
    {
        login = login.Trim().TrimStart('@').ToLowerInvariant();
        if (login.Contains('/')) login = login.TrimEnd('/')[(login.TrimEnd('/').LastIndexOf('/') + 1)..]; // twitch.tv/name
        if (login.Length == 0) throw new ActionException(L.T("Nome del canale mancante"));
        var r = await HelixAsync(HttpMethod.Get, "users", $"login={Uri.EscapeDataString(login)}");
        var u = r?["data"]?[0] ?? throw new ActionException(L.F("Canale «{0}» non trovato", login));
        return (J.Str(u["id"])!, J.Str(u["display_name"]) ?? login);
    }

    public async Task<JsonArray> SearchCategoriesAsync(string q)
    {
        var r = await HelixAsync(HttpMethod.Get, "search/categories", $"query={Uri.EscapeDataString(q)}&first=15");
        return new JsonArray((r?["data"] as JsonArray ?? []).Select(c => (JsonNode?)new JsonObject
        {
            ["id"] = J.Str(c?["id"]),
            ["name"] = J.Str(c?["name"]),
            ["art"] = J.Str(c?["box_art_url"])?.Replace("{width}", "52").Replace("{height}", "72"),
        }).ToArray());
    }

    // ---- Live sources -------------------------------------------------------------------------

    private void PublishDisconnected()
    {
        _live.Publish("twitch.conn", LiveService.V(L.T("Non connesso"), state: false));
        _live.Publish("twitch.live", LiveService.V("—", state: false, sub: L.T("Twitch non connesso")));
    }

    public void PublishLive(bool live, string? startedAt)
    {
        _live.Publish("twitch.live", LiveService.V(live ? L.T("IN DIRETTA") : "Offline", state: live));
        var started = live && DateTimeOffset.TryParse(startedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var s) ? s : (DateTimeOffset?)null;
        var uptime = LiveService.V(started is null ? "Offline" : LiveStateStore.FormatMs((long)(DateTimeOffset.UtcNow - started.Value).TotalMilliseconds, false), state: started is not null);
        if (started is not null)
            uptime["timer"] = new JsonObject
            {
                ["mode"] = "stopwatch",
                ["durationMs"] = 0,
                ["elapsedMs"] = (long)(DateTimeOffset.UtcNow - started.Value).TotalMilliseconds,
                ["running"] = true,
                // Absolute start: devices compute the time themselves (no per-second broadcast).
                ["startedAtMs"] = started.Value.ToUnixTimeMilliseconds(),
            };
        _live.Publish("twitch.uptime", uptime);
    }

    public void PublishChatSettings(JsonObject s)
    {
        void P(string mode, bool on, string? sub = null) =>
            _live.Publish($"twitch.chat:{mode}", LiveService.V(on ? "ON" : "OFF", state: on, sub: sub));
        var slow = J.Bool(s["slow_mode"]) ?? false;
        var slowSec = J.Int(s["slow_mode_wait_time"]) ?? J.Int(s["slow_mode_wait_time_seconds"]);
        P("slow", slow, slow && slowSec is not null ? $"{slowSec} s" : null);
        var fol = J.Bool(s["follower_mode"]) ?? false;
        var folMin = J.Int(s["follower_mode_duration"]) ?? J.Int(s["follower_mode_duration_minutes"]);
        P("follower", fol, fol && folMin is > 0 ? $"{folMin} min" : null);
        P("sub", J.Bool(s["subscriber_mode"]) ?? false);
        P("emote", J.Bool(s["emote_mode"]) ?? false);
    }

    /// <summary>Last follower / sub / raid: kept in settings so they survive a restart.</summary>
    public void PublishLast(string kind, string text, string? sub, bool save = true)
    {
        _live.Publish($"twitch.last:{kind}", LiveService.V(text, sub: sub));
        if (save) UpdateSettings(o =>
        {
            var last = o["last"] as JsonObject ?? new JsonObject();
            last[kind] = new JsonObject { ["text"] = text, ["sub"] = sub };
            o["last"] = last;
        });
    }

    private void PublishSaved()
    {
        if (Settings()["last"] is JsonObject last)
            foreach (var (k, v) in last) PublishLast(k, J.Str(v?["text"]) ?? "—", J.Str(v?["sub"]), save: false);
        foreach (var k in new[] { "follower", "sub", "raid", "cheer", "redeem" })
            if (Settings()["last"]?[k] is null) _live.Publish($"twitch.last:{k}", LiveService.V("—"));
    }

    /// <summary>Requests a refresh of the polled values soon (after an action that changes them).</summary>
    public void Kick() => _nextPoll = DateTimeOffset.Now.AddSeconds(1);

    private async Task LoopAsync(CancellationToken ct)
    {
        PublishSaved();
        if (!LoggedIn) PublishDisconnected();
        else _ = EventSub.EnsureRunningAsync();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (LoggedIn)
                {
                    if (DateTimeOffset.Now >= _nextValidate)
                    {
                        _nextValidate = DateTimeOffset.Now.AddMinutes(5); // retry soon if offline
                        var access = _secrets.Get(AccessSecret);
                        if (access is null || !await ValidateAsync(access))
                        {
                            var fresh = await RefreshAsync(access ?? "");
                            if (fresh is not null) await ValidateAsync(fresh);
                        }
                    }
                    if (_live.HasClients && DateTimeOffset.Now >= _nextPoll)
                    {
                        _nextPoll = DateTimeOffset.Now.AddSeconds(60);
                        await PollAsync();
                    }
                }
            }
            catch (ActionException ex) { _error = ex.Message; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { _error = L.T("Twitch non risponde: il PC è connesso a Internet?"); }
            catch (Exception ex) { Log.Error("Twitch loop failed", ex); }
            try { await Task.Delay(1000, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task PollAsync()
    {
        var id = RequireUser();
        var streams = await HelixAsync(HttpMethod.Get, "streams", $"user_id={id}");
        var stream = streams?["data"]?[0];
        PublishLive(stream is not null, J.Str(stream?["started_at"]));
        var viewers = J.Int(stream?["viewer_count"]);
        _live.Publish("twitch.viewers", viewers is null ? LiveService.V("—", sub: "offline") : LiveService.V(viewers.Value.ToString(), viewers));

        var fol = await HelixAsync(HttpMethod.Get, "channels/followers", $"broadcaster_id={id}&first=1");
        if (J.Int(fol?["total"]) is { } total) _live.Publish("twitch.followers", LiveService.V(total.ToString("N0", L.Culture), total));
        if (fol?["data"]?[0] is JsonObject f && Settings()["last"]?["follower"] is null)
            PublishLast("follower", J.Str(f["user_name"]) ?? "—", null);

        try
        {
            var subs = await HelixAsync(HttpMethod.Get, "subscriptions", $"broadcaster_id={id}&first=1");
            if (J.Int(subs?["total"]) is { } st) _live.Publish("twitch.subs", LiveService.V(st.ToString("N0", L.Culture), st));
        }
        catch (ActionException) { _live.Publish("twitch.subs", LiveService.V("—", sub: L.T("non affiliato"))); }

        var ch = await HelixAsync(HttpMethod.Get, "channels", $"broadcaster_id={id}");
        if (ch?["data"]?[0] is JsonObject info) _live.Publish("twitch.title", LiveService.V(J.Str(info["title"]) ?? "", sub: J.Str(info["game_name"])));

        var chat = await HelixAsync(HttpMethod.Get, "chat/settings", $"broadcaster_id={id}&moderator_id={id}");
        if (chat?["data"]?[0] is JsonObject cs) PublishChatSettings(cs);
    }

    /// <summary>Current chat settings (for the chat-mode toggles).</summary>
    public async Task<JsonObject> ChatSettingsAsync()
    {
        var id = RequireUser();
        var r = await HelixAsync(HttpMethod.Get, "chat/settings", $"broadcaster_id={id}&moderator_id={id}");
        return r?["data"]?[0] as JsonObject ?? new JsonObject();
    }

    /// <summary>"Prova connessione": validates the token now.</summary>
    public async Task TestAsync()
    {
        var access = _secrets.Get(AccessSecret) ?? await RefreshAsync("") ?? throw new ActionException(L.T("Twitch non collegato"));
        if (!await ValidateAsync(access))
        {
            var fresh = await RefreshAsync(access) ?? throw new ActionException(L.T("Accesso a Twitch scaduto: accedi di nuovo"));
            if (!await ValidateAsync(fresh)) throw new ActionException(L.T("Token non valido: accedi di nuovo"));
        }
        Kick();
        _ = EventSub.EnsureRunningAsync();
    }

    // ---- HTTP helpers -------------------------------------------------------------------------

    private static async Task<JsonObject> PostFormAsync(string url, Dictionary<string, string> form)
    {
        var (status, body) = await PostFormRawAsync(url, form, CancellationToken.None);
        if (status != HttpStatusCode.OK) throw new ActionException($"Twitch: {J.Str(body["message"]) ?? L.F("errore {0}", (int)status)}");
        return body;
    }

    private static async Task<(HttpStatusCode, JsonObject)> PostFormRawAsync(string url, Dictionary<string, string> form, CancellationToken ct)
    {
        using var res = await Http.PostAsync(url, new FormUrlEncodedContent(form), ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        JsonObject body;
        try { body = JsonNode.Parse(text) as JsonObject ?? new JsonObject(); }
        catch { body = new JsonObject { ["message"] = text.Length > 200 ? text[..200] : text }; }
        return (res.StatusCode, body);
    }

    public void Dispose()
    {
        _cts.Cancel();
        EventSub.Dispose();
    }
}
