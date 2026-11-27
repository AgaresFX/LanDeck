using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using PadNook.Actions;
using PadNook.Config;
using PadNook.Core;
using PadNook.Live;
using PadNook.Platform;
using PadNook.Plugins;
using PadNook.Security;
using Microsoft.AspNetCore.Http;

namespace PadNook.Web;

/// <summary>
/// WebSocket endpoint: authentication, key presses and live editing.
/// Message format: { "t": "type", "rid"?: "request id", ...payload }.
/// </summary>
public sealed class WsHub
{
    private static readonly TimeSpan EditTokenTtl = TimeSpan.FromHours(12);
    private static readonly TimeSpan HomeAppCodeTtl = TimeSpan.FromMinutes(10);

    private readonly ConfigStore _config;
    private readonly SecurityStore _security;
    private readonly PairingService _pairing;
    private readonly ActionExecutor _executor;
    private readonly ConcurrentDictionary<Guid, WsSession> _sessions = new();
    private readonly ConcurrentDictionary<string, (string DeviceId, DateTimeOffset Expires)> _editTokens = new();

    /// <summary>Raised when an authenticated client connects or disconnects.</summary>
    public event Action? ConnectionsChanged;

    private readonly ToggleStore _toggles;
    private LiveService? _live;
    private SliderControl? _sliders;
    private ObsClient? _obs;
    private PluginHost? _plugins;
    private SecretStore? _secrets;
    private HttpPoller? _http;
    private TwitchService? _twitch;
    private SlobsClient? _slobs;
    private DiscordClient? _discord;
    private LightroomClient? _lightroom;
    private StreamlabsSocket? _streamlabs;

    public WsHub(ConfigStore config, SecurityStore security, PairingService pairing, ActionExecutor executor, ToggleStore toggles)
    {
        _toggles = toggles;
        _config = config;
        _security = security;
        _pairing = pairing;
        _executor = executor;
        _security.DevicesChanged += () => _ = BroadcastDevicesAsync();
    }

    /// <summary>Connects the real-time services (created after the hub, they need to know if anyone is connected).</summary>
    public void AttachServices(LiveService live, ObsClient obs, PluginHost plugins, SecretStore secrets, HttpPoller http, TwitchService twitch, SlobsClient slobs, StreamlabsSocket streamlabs, LightroomClient lightroom, DiscordClient discord)
    {
        _slobs = slobs;
        _discord = discord;
        _streamlabs = streamlabs;
        _twitch = twitch;
        _secrets = secrets;
        _http = http;
        _live = live;
        _sliders = new SliderControl(live, lightroom);
        _lightroom = lightroom;
        _obs = obs;
        _plugins = plugins;
        live.Changed += values =>
            _ = BroadcastRawAsync(new JsonObject { ["t"] = "live", ["values"] = values }.ToJsonString(), except: null);
        live.State.TimerFinished += (name, _pc, phone) =>
        {
            if (!phone) return;
            var title = LiveStateStore.TimerTitle(_config.Snapshot().Profiles, name);
            _ = BroadcastRawAsync(new JsonObject { ["t"] = "timer.done", ["name"] = name, ["title"] = title }.ToJsonString(), except: null);
        };
        live.ForegroundChanged += fg =>
            _ = BroadcastRawAsync(new JsonObject { ["t"] = "fg", ["exe"] = fg.Exe, ["title"] = fg.Title }.ToJsonString(), except: null);
        plugins.Reloaded += () =>
        {
            var msg = plugins.Describe();
            msg["t"] = "plugins";
            _ = BroadcastRawAsync(msg.ToJsonString(), except: null);
        };
    }

    public bool IsEditTokenValid(string? token, string deviceId) =>
        !string.IsNullOrEmpty(token)
        && _editTokens.TryGetValue(token, out var et)
        && et.DeviceId == deviceId
        && et.Expires > DateTimeOffset.Now;

    public bool IsOnline(string deviceId) => _sessions.Values.Any(s => s.DeviceId == deviceId);

    public int OnlineCount => _sessions.Values.Count(s => s.DeviceId is not null);

    // ---- Connection --------------------------------------------------------------------------

    public async Task HandleAsync(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        if (!OriginAllowed(ctx.Request))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
        var session = new WsSession(socket, ctx.Connection.RemoteIpAddress?.ToString() ?? "?");
        _sessions[session.Id] = session;
        try
        {
            await session.ReceiveLoopAsync(msg => DispatchAsync(session, msg), ctx.RequestAborted);
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (Exception ex) { Log.Error("WebSocket session error", ex); }
        finally
        {
            _sessions.TryRemove(session.Id, out _);
            if (session.DeviceId is not null)
            {
                ConnectionsChanged?.Invoke();
                _ = BroadcastDevicesAsync();
            }
        }
    }

    /// <summary>Blocks cross-site WebSocket hijacking: the page must come from this server.</summary>
    private static bool OriginAllowed(HttpRequest req)
    {
        var origin = req.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin)) return true; // non-browser client
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        if (string.Equals(uri.Authority, req.Host.Value, StringComparison.OrdinalIgnoreCase)) return true;
#if DEBUG
        // Vite dev server proxy
        if (uri.IsLoopback || uri.Port == 5173) return true;
#endif
        return false;
    }

    // ---- Dispatch ----------------------------------------------------------------------------

    private async Task DispatchAsync(WsSession s, JsonObject msg)
    {
        var t = J.Str(msg["t"]);
        var rid = J.Str(msg["rid"]);
        try
        {
            switch (t)
            {
                case "ping": await s.SendAsync(new JsonObject { ["t"] = "pong" }); return;
                case "hello": await HelloAsync(s, msg); return;
                case "pair": await PairAsync(s, msg, rid); return;
            }

            if (s.DeviceId is null)
            {
                await SendErrorAsync(s, rid, "auth", L.T("Dispositivo non associato"));
                return;
            }

            switch (t)
            {
                case "press": Press(s, msg, rid); return;
                case "slider": Slider(s, msg, rid, tap: false); return;
                case "slider.tap": Slider(s, msg, rid, tap: true); return;
                case "pair.code.request": await PairCodeRequestAsync(s, rid); return;
                case "edit.unlock": await UnlockAsync(s, msg, rid); return;
                case "edit.pin.set": await SetPinAsync(s, msg, rid); return;
                case "edit.lock": await LockAsync(s, rid); return;
            }

            if (t?.StartsWith("edit.", StringComparison.Ordinal) == true && !s.Edit)
            {
                await SendErrorAsync(s, rid, "locked", L.T("Modalità modifica bloccata: inserisci il PIN"));
                return;
            }

            switch (t)
            {
                case "edit.profile.put": await ProfilePutAsync(s, msg, rid); return;
                case "edit.profile.delete": await ProfileDeleteAsync(s, msg, rid); return;
                case "edit.devices.list": await s.SendAsync(DevicesMessage()); return;
                case "edit.device.update": await DeviceUpdateAsync(s, msg, rid); return;
                case "edit.device.revoke":
                {
                    var id = J.Str(msg["deviceId"]) ?? throw new ArgumentException(L.T("Id dispositivo mancante"));
                    await KickDeviceAsync(id);
                    if (!_security.Revoke(id)) throw new ArgumentException(L.T("Dispositivo non trovato"));
                    return;
                }
                case "edit.toggle.set":
                {
                    var keyId = J.Str(msg["keyId"]) ?? throw new ArgumentException(L.T("Id tasto mancante"));
                    var on = J.Bool(msg["on"]) ?? false;
                    _toggles.Set(keyId, on);
                    await BroadcastRawAsync(new JsonObject { ["t"] = "toggle.state", ["keyId"] = keyId, ["on"] = on }.ToJsonString(), except: null);
                    return;
                }
                case "edit.timer":
                {
                    // Editor test buttons / config changes: run a timer action directly (no key lookup).
                    var action = new JsonObject
                    {
                        ["type"] = "timer",
                        ["name"] = J.Str(msg["name"]) ?? "",
                        ["op"] = J.Str(msg["op"]) ?? "toggle",
                        ["mode"] = J.Str(msg["mode"]),
                        ["durationSec"] = J.Num(msg["durationSec"]),
                        ["alertPc"] = J.Bool(msg["alertPc"]),
                        ["alertPhone"] = J.Bool(msg["alertPhone"]),
                    };
                    var error = await _executor.ExecuteAsync(action, new ActionContext(s.DeviceId!, _executor));
                    if (error is not null) await SendErrorAsync(s, rid, "timer", error);
                    return;
                }
                case "edit.info": await InfoAsync(s, rid); return;
                case "edit.weather.search":
                {
                    var q = J.Str(msg["q"])?.Trim() ?? "";
                    if (q.Length < 2) throw new ArgumentException(L.T("Scrivi almeno 2 lettere"));
                    JsonArray results;
                    try { results = await WeatherService.SearchAsync(q); }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                    {
                        throw new InvalidOperationException(L.T("Ricerca non riuscita: il PC è connesso a Internet?"));
                    }
                    await s.SendAsync(new JsonObject { ["t"] = "weather.results", ["rid"] = rid, ["results"] = results });
                    return;
                }
                case "edit.obs.set":
                {
                    var port = J.Int(msg["port"]) ?? 4455;
                    if (port is < 1 or > 65535) throw new ArgumentException(L.T("Porta non valida"));
                    _obs?.UpdateSettings(J.Bool(msg["enabled"]) ?? true, port, J.Str(msg["password"]));
                    await s.SendAsync(new JsonObject { ["t"] = "obs.saved", ["rid"] = rid });
                    return;
                }
                case "edit.secrets.list": await s.SendAsync(SecretsMessage(rid)); return;
                case "edit.secrets.set":
                {
                    var name = SecretStore.CleanName(J.Str(msg["name"]));
                    var value = J.Str(msg["value"]) ?? "";
                    if (value.Length == 0) throw new ArgumentException(L.T("Valore della credenziale mancante"));
                    _secrets!.Set(name, value);
                    await s.SendAsync(SecretsMessage(rid));
                    return;
                }
                case "edit.secrets.delete":
                    _secrets!.Delete(J.Str(msg["name"]) ?? "");
                    await s.SendAsync(SecretsMessage(rid));
                    return;
                case "edit.http.test":
                {
                    var spec = msg["request"] as JsonObject ?? throw new ArgumentException(L.T("Richiesta mancante"));
                    var res = await _http!.TestAsync(spec);
                    res["t"] = "http.result";
                    res["rid"] = rid;
                    await s.SendAsync(res);
                    return;
                }
                case "edit.slobs.set":
                {
                    var port = J.Int(msg["port"]) ?? 59650;
                    if (port is < 1 or > 65535) throw new ArgumentException(L.T("Porta non valida"));
                    _slobs!.UpdateSettings(J.Bool(msg["enabled"]) ?? true, J.Str(msg["mode"]) ?? "pipe", port, J.Str(msg["token"]));
                    await s.SendAsync(new JsonObject { ["t"] = "slobs.saved", ["rid"] = rid });
                    return;
                }
                case "edit.streamlabs.info": await SendStreamlabsAsync(s, rid); return;
                case "edit.streamlabs.set":
                    _streamlabs!.Update(msg);
                    await SendStreamlabsAsync(s, rid);
                    return;
                case "edit.streamlabs.test":
                {
                    var text = await _streamlabs!.TestAsync();
                    var o = _streamlabs.Info();
                    o["t"] = "streamlabs.info";
                    o["rid"] = rid;
                    o["test"] = text;
                    await s.SendAsync(o);
                    return;
                }
                case "edit.action.test":
                {
                    // Editor "Prova": runs an action as if pressed, off the receive loop (macros can be long).
                    var action = msg["action"] as JsonObject ?? throw new ArgumentException(L.T("Azione mancante"));
                    var ctx = new ActionContext(s.DeviceId!, _executor);
                    _ = Task.Run(async () =>
                    {
                        var error = await _executor.ExecuteAsync(action, ctx);
                        await s.SendAsync(new JsonObject { ["t"] = "action.test", ["rid"] = rid, ["ok"] = error is null, ["error"] = error });
                    });
                    return;
                }
                case "edit.discord.info":
                    await s.SendAsync(new JsonObject { ["t"] = "discord.info", ["rid"] = rid, ["discord"] = _discord!.Info() });
                    return;
                case "edit.discord.set":
                    _discord!.UpdateSettings(J.Bool(msg["enabled"]) ?? true, J.Str(msg["clientId"]) ?? "", J.Str(msg["secret"]));
                    await s.SendAsync(new JsonObject { ["t"] = "discord.info", ["rid"] = rid, ["discord"] = _discord.Info() });
                    return;
                case "edit.discord.logout":
                    _discord!.Logout();
                    await s.SendAsync(new JsonObject { ["t"] = "discord.info", ["rid"] = rid, ["discord"] = _discord.Info() });
                    return;
                case "edit.discord.login":
                    // The user has up to 2 minutes to confirm in Discord: answer later, off the receive loop.
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _discord!.LoginAsync();
                            await s.SendAsync(new JsonObject { ["t"] = "discord.info", ["rid"] = rid, ["discord"] = _discord.Info() });
                        }
                        catch (ActionException ex) { await SendErrorAsync(s, rid, "invalid", ex.Message); }
                        catch (Exception ex)
                        {
                            Log.Error("Discord login failed", ex);
                            await SendErrorAsync(s, rid, "invalid", L.T("Accesso a Discord non riuscito"));
                        }
                    });
                    return;
                case "edit.mouse.pos":
                {
                    var (x, y) = Platform.Win32Input.CursorPosition();
                    await s.SendAsync(new JsonObject { ["t"] = "mouse.pos", ["rid"] = rid, ["x"] = x, ["y"] = y });
                    return;
                }
                case "edit.lightroom.info":
                    await s.SendAsync(new JsonObject { ["t"] = "lightroom.info", ["rid"] = rid, ["lightroom"] = _lightroom!.Info() });
                    return;
                case "edit.lightroom.install":
                    await Task.Run(LightroomClient.InstallPlugin);
                    await s.SendAsync(new JsonObject { ["t"] = "lightroom.info", ["rid"] = rid, ["lightroom"] = _lightroom!.Info() });
                    return;
                case "edit.lightroom.openFolder":
                {
                    var folder = Directory.Exists(LightroomClient.PluginFolder) ? LightroomClient.PluginFolder : Path.GetDirectoryName(LightroomClient.PluginFolder)!;
                    Directory.CreateDirectory(folder);
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true })?.Dispose();
                    return;
                }
                case "edit.lightroom.presets":
                {
                    var names = await _lightroom!.PresetsAsync();
                    await s.SendAsync(new JsonObject
                    {
                        ["t"] = "lightroom.presets",
                        ["rid"] = rid,
                        ["presets"] = new JsonArray(names.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()),
                    });
                    return;
                }
                case "edit.twitch.info": await SendTwitchAsync(s, rid); return;
                case "edit.twitch.set":
                    _twitch!.UpdateSettings(o =>
                    {
                        if (msg["clientId"] is JsonValue cid) o["clientId"] = (J.Str(cid) ?? "").Trim();
                        if (msg["rules"] is JsonArray rules) o["rules"] = rules.DeepClone();
                    });
                    await SendTwitchAsync(s, rid);
                    return;
                case "edit.twitch.login":
                    await _twitch!.StartLoginAsync();
                    await SendTwitchAsync(s, rid);
                    return;
                case "edit.twitch.cancel": _twitch!.CancelLogin(); await SendTwitchAsync(s, rid); return;
                case "edit.twitch.logout": _twitch!.Logout(); await SendTwitchAsync(s, rid); return;
                case "edit.twitch.test":
                    await _twitch!.TestAsync();
                    await SendTwitchAsync(s, rid);
                    return;
                case "edit.twitch.categories":
                {
                    var q = J.Str(msg["q"])?.Trim() ?? "";
                    if (q.Length < 2) throw new ArgumentException(L.T("Scrivi almeno 2 lettere"));
                    var results = await _twitch!.SearchCategoriesAsync(q);
                    await s.SendAsync(new JsonObject { ["t"] = "twitch.categories", ["rid"] = rid, ["results"] = results });
                    return;
                }
                case "edit.soundpad.get":
                {
                    var st = await Task.Run(SoundBoard.Status);
                    st["t"] = "soundpad.status";
                    st["rid"] = rid;
                    await s.SendAsync(st);
                    return;
                }
                case "edit.soundpad.set":
                {
                    var settings = msg["settings"] as JsonObject ?? throw new ArgumentException(L.T("Impostazioni mancanti"));
                    await Task.Run(() => SoundBoard.UpdateSettings(settings));
                    var st = await Task.Run(SoundBoard.Status);
                    st["t"] = "soundpad.status";
                    st["rid"] = rid;
                    await s.SendAsync(st);
                    return;
                }
                case "edit.soundpad.refresh":
                {
                    await Task.Run(SoundBoard.Refresh);
                    var st = await Task.Run(SoundBoard.Status);
                    st["t"] = "soundpad.status";
                    st["rid"] = rid;
                    await s.SendAsync(st);
                    return;
                }
                case "edit.soundpad.installCable":
                {
                    var error = await CableInstaller.InstallAsync();
                    if (error is not null) await SendErrorAsync(s, rid, "soundpad", error);
                    else await s.SendAsync(new JsonObject { ["t"] = "soundpad.installing", ["rid"] = rid });
                    return;
                }
                case "edit.soundpad.cablePage": CableInstaller.OpenHomePage(); return;
                case "edit.sound.test":
                {
                    var action = new JsonObject
                    {
                        ["type"] = "sound",
                        ["command"] = J.Str(msg["command"]) ?? "play",
                        ["file"] = J.Str(msg["file"]) ?? "",
                        ["volume"] = J.Int(msg["volume"]) ?? 100,
                        ["mode"] = J.Str(msg["mode"]) ?? "restart",
                    };
                    var error = await _executor.ExecuteAsync(action, new ActionContext(s.DeviceId!, _executor));
                    if (error is not null) await SendErrorAsync(s, rid, "sound", error);
                    else await s.SendAsync(new JsonObject { ["t"] = "sound.ok", ["rid"] = rid });
                    return;
                }
                case "edit.plugins.reload": _plugins?.Reload(); return;
                case "edit.plugins.openFolder":
                    if (_plugins is not null)
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_plugins.Folder) { UseShellExecute = true })?.Dispose();
                    return;
                default: await SendErrorAsync(s, rid, "unknown", L.F("Messaggio sconosciuto: {0}", t)); return;
            }
        }
        catch (ArgumentException ex) { await SendErrorAsync(s, rid, "invalid", ex.Message); }
        catch (ActionException ex) { await SendErrorAsync(s, rid, "invalid", ex.Message); }
        catch (InvalidOperationException ex) { await SendErrorAsync(s, rid, "invalid", ex.Message); }
        catch (Exception ex)
        {
            Log.Error($"Error handling '{t}'", ex);
            await SendErrorAsync(s, rid, "server", L.T("Errore interno del server"));
        }
    }

    // ---- Auth & pairing ----------------------------------------------------------------------

    private async Task HelloAsync(WsSession s, JsonObject msg)
    {
        // The desktop editor window (WebView2 inside this process) gets a per-process key injected
        // into its page: it is always allowed to edit, without pairing or PIN. Loopback only.
        if (J.Str(msg["desktopKey"]) is { } dk)
        {
            if (!DesktopEditorAuth.Matches(dk) || !System.Net.IPAddress.TryParse(s.Remote, out var ip) || !System.Net.IPAddress.IsLoopback(ip))
            {
                await SendErrorAsync(s, null, "auth", L.T("Editor non autorizzato"));
                return;
            }
            s.DeviceId = DesktopEditorAuth.DeviceId;
            s.Edit = true;
            await SendWelcomeAsync(s, new DeviceRecord { Id = DesktopEditorAuth.DeviceId, Name = "Editor PC" });
            ConnectionsChanged?.Invoke();
            return;
        }

        var token = J.Str(msg["token"]);
        var device = token is null ? null : _security.Authenticate(token);
        if (device is null)
        {
            await SendErrorAsync(s, null, "auth", L.T("Dispositivo non riconosciuto: associalo di nuovo"));
            return;
        }
        s.DeviceId = device.Id;

        var editToken = J.Str(msg["editToken"]);
        if (editToken is not null && _editTokens.TryGetValue(editToken, out var et)
            && et.DeviceId == device.Id && et.Expires > DateTimeOffset.Now)
        {
            s.Edit = true;
            s.EditToken = editToken;
        }

        await SendWelcomeAsync(s, device);
        ConnectionsChanged?.Invoke();
        _ = BroadcastDevicesAsync();
    }

    private async Task PairAsync(WsSession s, JsonObject msg, string? rid)
    {
        switch (_pairing.TryConsume(J.Str(msg["code"])))
        {
            case PairResult.RateLimited:
                await SendErrorAsync(s, rid, "rateLimited", L.T("Troppi tentativi: riprova tra un minuto"));
                return;
            case PairResult.Invalid:
                await SendErrorAsync(s, rid, "pairInvalid", L.T("Codice non valido o scaduto"));
                return;
        }

        var (device, token) = _security.CreateDevice(J.Str(msg["deviceName"]) ?? L.T("Dispositivo"));
        s.DeviceId = device.Id;
        await s.SendAsync(new JsonObject
        {
            ["t"] = "paired",
            ["rid"] = rid,
            ["deviceId"] = device.Id,
            ["deviceName"] = device.Name,
            ["token"] = token,
        });
        await SendWelcomeAsync(s, device);
        ConnectionsChanged?.Invoke();
        _ = BroadcastDevicesAsync(); // now the new device shows as online
    }

    private async Task PairCodeRequestAsync(WsSession s, string? rid)
    {
        var (code, expires) = _pairing.CreateCode(HomeAppCodeTtl);
        await s.SendAsync(new JsonObject
        {
            ["t"] = "pair.code",
            ["rid"] = rid,
            ["code"] = code,
            ["expiresInSec"] = (int)(expires - DateTimeOffset.Now).TotalSeconds,
        });
    }

    private async Task SendWelcomeAsync(WsSession s, DeviceRecord device)
    {
        var (rev, profiles) = _config.Snapshot();
        await s.SendAsync(new JsonObject
        {
            ["t"] = "welcome",
            ["deviceId"] = device.Id,
            ["deviceName"] = device.Name,
            ["defaultProfileId"] = device.DefaultProfileId,
            ["rev"] = rev,
            ["profiles"] = profiles,
            ["toggles"] = _toggles.Snapshot(),
            ["edit"] = s.Edit,
            ["hasPin"] = _security.HasPin,
            ["serverVersion"] = Program.Version,
            ["live"] = _live?.Snapshot() ?? new JsonObject(),
            ["fg"] = _live?.Foreground is { } fg ? new JsonObject { ["exe"] = fg.Exe, ["title"] = fg.Title } : null,
            ["plugins"] = _plugins?.Describe(),
        });
    }

    /// <summary>Lists for the editor: audio devices and apps, recent foreground apps, OBS scenes.</summary>
    private async Task InfoAsync(WsSession s, string? rid)
    {
        var outputs = new JsonArray();
        try
        {
            foreach (var d in CoreAudio.ListOutputs())
                outputs.Add(new JsonObject { ["id"] = d.Id, ["name"] = d.Name, ["isDefault"] = d.IsDefault });
        }
        catch (Exception ex) { Log.Error("Unable to list audio devices", ex); }
        var inputs = new JsonArray();
        try
        {
            foreach (var d in CoreAudio.ListInputs())
                inputs.Add(new JsonObject { ["id"] = d.Id, ["name"] = d.Name, ["isDefault"] = d.IsDefault });
        }
        catch (Exception ex) { Log.Error("Unable to list microphones", ex); }

        // Apps playing audio, with their volume and icon (the editor shows them as a picker).
        var audioApps = new JsonArray();
        try
        {
            var names = CoreAudio.ListSessionApps();
            var states = CoreAudio.GetAppStates(names.ToHashSet());
            foreach (var n in names)
            {
                var st = states.TryGetValue(n, out var x) ? x : ((float Volume, bool Muted)?)null;
                audioApps.Add(new JsonObject
                {
                    ["name"] = n,
                    ["volume"] = st is null ? null : Math.Round(st.Value.Volume * 100),
                    ["muted"] = st?.Muted ?? false,
                    ["icon"] = AppIcons.DataUrl(n),
                });
            }
        }
        catch (Exception ex) { Log.Error("Unable to list audio apps", ex); }
#if DEBUG
        if (audioApps.Count == 0 && Environment.GetEnvironmentVariable("PADNOOK_DEV_FAKEAUDIO") == "1")
        {
            audioApps.Add(new JsonObject { ["name"] = "explorer", ["volume"] = 72, ["muted"] = false, ["icon"] = AppIcons.DataUrl("explorer") });
            audioApps.Add(new JsonObject { ["name"] = "msedgewebview2", ["volume"] = 35, ["muted"] = true, ["icon"] = AppIcons.DataUrl("msedgewebview2") });
        }
#endif

        var recent = _live?.RecentApps().Select(a => a.Exe).ToList() ?? [];
        var self = System.Diagnostics.Process.GetCurrentProcess().ProcessName.ToLowerInvariant();
        var apps = recent.Concat(WindowControl.WindowedApps()).Where(a => a != self).Distinct().ToList();
        await s.SendAsync(new JsonObject
        {
            ["t"] = "info",
            ["rid"] = rid,
            ["outputs"] = outputs,
            ["inputs"] = inputs,
            ["sessions"] = new JsonArray(CoreAudio.ListSessionApps().Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()),
            ["audioApps"] = audioApps,
            ["apps"] = new JsonArray(apps.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()),
            ["screenshotFolder"] = SystemControl.DefaultScreenshotFolder,
            ["obs"] = _obs is null ? null : await _obs.InfoAsync(),
            ["secrets"] = SecretNames(),
            ["twitch"] = _twitch?.Info(),
            ["slobs"] = _slobs is null ? null : await _slobs.InfoAsync(),
            ["streamlabs"] = _streamlabs?.Info(),
            ["lightroom"] = _lightroom?.Info(),
            ["discord"] = _discord?.Info(),
        });
    }

    private Task SendStreamlabsAsync(WsSession s, string? rid)
    {
        var o = _streamlabs!.Info();
        o["t"] = "streamlabs.info";
        o["rid"] = rid;
        return s.SendAsync(o);
    }

    private Task SendTwitchAsync(WsSession s, string? rid)
    {
        var o = _twitch!.Info();
        o["t"] = "twitch.info";
        o["rid"] = rid;
        return s.SendAsync(o);
    }

    /// <summary>Makes a key blink on every device (event rules).</summary>
    public Task FlashAsync(string keyId, string color, int ms) =>
        BroadcastRawAsync(new JsonObject { ["t"] = "flash", ["keyId"] = keyId, ["color"] = color, ["ms"] = ms }.ToJsonString(), except: null);

    /// <summary>Only the names: the values never leave the PC.</summary>
    private JsonArray SecretNames() =>
        new((_secrets?.Names() ?? []).Select(n => (JsonNode?)JsonValue.Create(n)).ToArray());

    private JsonObject SecretsMessage(string? rid) => new() { ["t"] = "secrets", ["rid"] = rid, ["names"] = SecretNames() };

    // ---- Edit mode ---------------------------------------------------------------------------

    private async Task UnlockAsync(WsSession s, JsonObject msg, string? rid)
    {
        if (!_security.HasPin)
        {
            await SendErrorAsync(s, rid, "noPin", L.T("Nessun PIN impostato"));
            return;
        }
        if (s.PinLockedUntil > DateTimeOffset.Now)
        {
            await SendErrorAsync(s, rid, "rateLimited", L.T("Troppi tentativi: attendi qualche secondo"));
            return;
        }
        var pin = J.Str(msg["pin"]) ?? "";
        if (!_security.VerifyPin(pin))
        {
            s.PinFailures++;
            if (s.PinFailures >= 5)
            {
                s.PinFailures = 0;
                s.PinLockedUntil = DateTimeOffset.Now.AddSeconds(30);
            }
            await SendErrorAsync(s, rid, "wrongPin", L.T("PIN errato"));
            return;
        }
        s.PinFailures = 0;
        await GrantEditAsync(s, rid);
    }

    private async Task SetPinAsync(WsSession s, JsonObject msg, string? rid)
    {
        // The first PIN can be set by any paired device; changing it requires edit mode.
        if (_security.HasPin && !s.Edit)
        {
            await SendErrorAsync(s, rid, "locked", L.T("Sblocca la modalità modifica per cambiare il PIN"));
            return;
        }
        var pin = J.Str(msg["pin"]);
        if (!SecurityStore.IsValidPin(pin))
        {
            await SendErrorAsync(s, rid, "invalidPin", L.T("Il PIN deve avere da 4 a 8 cifre"));
            return;
        }
        _security.SetPin(pin!);
        await GrantEditAsync(s, rid);
    }

    private async Task GrantEditAsync(WsSession s, string? rid)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        _editTokens[token] = (s.DeviceId!, DateTimeOffset.Now + EditTokenTtl);
        s.Edit = true;
        s.EditToken = token;
        await s.SendAsync(new JsonObject { ["t"] = "edit.unlocked", ["rid"] = rid, ["editToken"] = token });
        await s.SendAsync(DevicesMessage());
    }

    private async Task LockAsync(WsSession s, string? rid)
    {
        if (s.EditToken is not null) _editTokens.TryRemove(s.EditToken, out _);
        s.Edit = false;
        s.EditToken = null;
        await s.SendAsync(new JsonObject { ["t"] = "edit.locked", ["rid"] = rid });
    }

    private async Task ProfilePutAsync(WsSession s, JsonObject msg, string? rid)
    {
        if (msg["profile"] is not JsonObject profile) throw new ArgumentException(L.T("Profilo mancante"));
        var rev = _config.PutProfile(profile);
        await s.SendAsync(new JsonObject { ["t"] = "profile.saved", ["rid"] = rid, ["rev"] = rev });
        var json = new JsonObject { ["t"] = "profile.changed", ["rev"] = rev, ["profile"] = profile.DeepClone() }.ToJsonString();
        await BroadcastRawAsync(json, except: s.Id);
    }

    private async Task ProfileDeleteAsync(WsSession s, JsonObject msg, string? rid)
    {
        var id = J.Str(msg["id"]) ?? throw new ArgumentException(L.T("Id profilo mancante"));
        var rev = _config.DeleteProfile(id);
        var json = new JsonObject { ["t"] = "profile.deleted", ["rid"] = rid, ["rev"] = rev, ["id"] = id }.ToJsonString();
        await BroadcastRawAsync(json, except: null);
    }

    private async Task DeviceUpdateAsync(WsSession s, JsonObject msg, string? rid)
    {
        var id = J.Str(msg["deviceId"]) ?? throw new ArgumentException(L.T("Id dispositivo mancante"));
        var name = J.Str(msg["name"]);
        var setDefault = msg.ContainsKey("defaultProfileId");
        var updated = _security.Update(id, name, setDefault, J.Str(msg["defaultProfileId"]))
                      ?? throw new ArgumentException(L.T("Dispositivo non trovato"));
        var json = new JsonObject
        {
            ["t"] = "device.updated",
            ["deviceName"] = updated.Name,
            ["defaultProfileId"] = updated.DefaultProfileId,
        }.ToJsonString();
        foreach (var target in _sessions.Values.Where(x => x.DeviceId == id))
            await target.SendRawAsync(json);
    }

    // ---- Presses -----------------------------------------------------------------------------

    private void Press(WsSession s, JsonObject msg, string? rid)
    {
        var profileId = J.Str(msg["profileId"]) ?? "";
        var pageId = J.Str(msg["pageId"]) ?? "";
        var keyId = J.Str(msg["keyId"]) ?? "";
        var gesture = J.Str(msg["gesture"]) ?? "press";

        var key = _config.FindKey(profileId, pageId, keyId);
        if (key is null)
        {
            _ = SendErrorAsync(s, rid, "notFound", L.T("Tasto non trovato (configurazione cambiata?)"));
            return;
        }

        JsonNode? actionNode;
        if (gesture == "long")
        {
            actionNode = key["onLongPress"];
        }
        else if (gesture == "double")
        {
            actionNode = key["onDoubleTap"];
        }
        else if (J.Str(key["kind"]) == "toggle")
        {
            // Toggle: flip the shared state, then run the action for the new state.
            var on = _toggles.Flip(keyId);
            _ = BroadcastRawAsync(new JsonObject { ["t"] = "toggle.state", ["keyId"] = keyId, ["on"] = on }.ToJsonString(), except: null);
            var toggle = key["toggle"];
            var same = J.Bool(toggle?["sameAction"]) ?? true;
            actionNode = on || same ? toggle?["onAction"] : toggle?["offAction"];
        }
        else
        {
            actionNode = key["onPress"];
        }
        if (actionNode is not JsonObject action) return;

        var ctx = new ActionContext(s.DeviceId!, _executor);
        // Run off the receive loop so the next press is never delayed by a slow action.
        _ = Task.Run(async () =>
        {
            var error = await _executor.ExecuteAsync(action, ctx);
            if (error is not null)
            {
                await s.SendAsync(new JsonObject
                {
                    ["t"] = "press.result",
                    ["rid"] = rid,
                    ["keyId"] = keyId,
                    ["ok"] = false,
                    ["error"] = error,
                });
            }
        });
    }

    /// <summary>Slider keys: "slider" sets the value (0..100), "slider.tap" toggles mute.</summary>
    private void Slider(WsSession s, JsonObject msg, string? rid, bool tap)
    {
        var keyId = J.Str(msg["keyId"]) ?? "";
        var key = _config.FindKey(J.Str(msg["profileId"]) ?? "", J.Str(msg["pageId"]) ?? "", keyId);
        if (key?["slider"] is not JsonObject cfg || _sliders is null)
        {
            _ = SendErrorAsync(s, rid, "notFound", L.T("Slider non trovato (configurazione cambiata?)"));
            return;
        }
        void Fail(string error) => _ = s.SendAsync(new JsonObject
        {
            ["t"] = "press.result",
            ["rid"] = rid,
            ["keyId"] = keyId,
            ["ok"] = false,
            ["error"] = error,
        });

        if (!tap)
        {
            _sliders.Set(cfg, J.Num(msg["value"]) ?? 0, Fail);
            return;
        }
        _ = Task.Run(() =>
        {
            try { _sliders.ToggleMute(cfg); }
            catch (ActionException ex) { Fail(ex.Message); }
            catch (Exception ex)
            {
                Log.Error("Slider tap failed", ex);
                Fail(L.T("Impossibile cambiare il muto"));
            }
        });
    }

    // ---- Devices & broadcast -----------------------------------------------------------------

    private JsonObject DevicesMessage()
    {
        var arr = new JsonArray();
        foreach (var d in _security.Devices())
        {
            arr.Add(new JsonObject
            {
                ["id"] = d.Id,
                ["name"] = d.Name,
                ["defaultProfileId"] = d.DefaultProfileId,
                ["pairedAt"] = d.PairedAt.ToString("o"),
                ["lastSeen"] = d.LastSeen.ToString("o"),
                ["online"] = IsOnline(d.Id),
            });
        }
        return new JsonObject { ["t"] = "devices", ["devices"] = arr };
    }

    private Task BroadcastDevicesAsync()
    {
        if (!_sessions.Values.Any(x => x.Edit)) return Task.CompletedTask;
        var json = DevicesMessage().ToJsonString();
        return Task.WhenAll(_sessions.Values.Where(x => x.Edit).Select(x => x.SendRawAsync(json)));
    }

    private Task BroadcastRawAsync(string json, Guid? except) =>
        Task.WhenAll(_sessions.Values
            .Where(x => x.DeviceId is not null && x.Id != except)
            .Select(x => x.SendRawAsync(json)));

    /// <summary>Disconnects every session of a revoked device.</summary>
    public async Task KickDeviceAsync(string deviceId)
    {
        var json = new JsonObject { ["t"] = "revoked" }.ToJsonString();
        foreach (var s in _sessions.Values.Where(x => x.DeviceId == deviceId).ToList())
        {
            s.DeviceId = null;
            s.Edit = false;
            await s.SendRawAsync(json);
            await s.CloseAsync(WebSocketCloseStatus.PolicyViolation, "revoked");
        }
        foreach (var kv in _editTokens.Where(kv => kv.Value.DeviceId == deviceId).ToList())
            _editTokens.TryRemove(kv.Key, out _);
    }

    private static Task SendErrorAsync(WsSession s, string? rid, string code, string message) =>
        s.SendAsync(new JsonObject { ["t"] = "error", ["rid"] = rid, ["code"] = code, ["message"] = message });
}
