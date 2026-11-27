using System.Globalization;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using PadNook.Actions;
using PadNook.Config;
using PadNook.Core;

namespace PadNook.Live;

/// <summary>
/// Streamlabs account: Socket API (socket.io v2 over WebSocket, events like donations, follows, bits)
/// and the v2.0 REST API (custom alerts). Tokens are pasted by the user and kept in the DPAPI store.
/// Live sources: streamlabs.conn, streamlabs.last:donation|event, streamlabs.total.
/// </summary>
public sealed class StreamlabsSocket : IDisposable
{
    private const string SocketSecret = "__streamlabs.socket";
    private const string AccessSecret = "__streamlabs.access";

    private static string? DevUrl(string name) =>
        Environment.GetEnvironmentVariable("PADNOOK_DEV") == "1" && Environment.GetEnvironmentVariable(name) is { Length: > 0 } u ? u.TrimEnd('/') : null;
    private static readonly string SocketBase = DevUrl("PADNOOK_DEV_STREAMLABS_SOCKET") ?? "wss://sockets.streamlabs.com";
    private static readonly string ApiBase = DevUrl("PADNOOK_DEV_STREAMLABS_API") ?? "https://streamlabs.com/api/v2.0";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly ConfigStore _config;
    private readonly SecretStore _secrets;
    private readonly LiveService _live;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _connCts;
    private Task? _loop;
    private volatile bool _connected;
    private string? _error;
    private double _total;
    private string? _totalCurrency;

    public event Action<StreamEvent>? Event;

    public StreamlabsSocket(ConfigStore config, SecretStore secrets, LiveService live)
    {
        _config = config;
        _secrets = secrets;
        _live = live;
    }

    private JsonObject Settings() => _config.SettingsSnapshot()["streamlabs"] as JsonObject ?? new JsonObject();

    public JsonArray Rules => Settings()["rules"] as JsonArray ?? [];

    public void Update(JsonObject msg)
    {
        var reconnect = false;
        if (J.Str(msg["socketToken"]) is { Length: > 0 } st)
        {
            _secrets.Set(SocketSecret, st.Trim());
            reconnect = true;
        }
        if (J.Str(msg["accessToken"]) is { Length: > 0 } at) _secrets.Set(AccessSecret, at.Trim());
        if (J.Bool(msg["clearTokens"]) == true)
        {
            reconnect = true;
            _secrets.Delete(SocketSecret);
            _secrets.Delete(AccessSecret);
        }
        _config.UpdateSettings(s =>
        {
            var o = s["streamlabs"] as JsonObject ?? new JsonObject();
            if (msg["rules"] is JsonArray rules) o["rules"] = rules.DeepClone();
            if (J.Bool(msg["enabled"]) is { } en)
            {
                o["enabled"] = en;
                reconnect = true;
            }
            s["streamlabs"] = o;
        });
        if (!reconnect) return; // rules or the Access Token only: the socket stays connected
        _connCts?.Cancel();
        Start();
    }

    public JsonObject Info() => new()
    {
        ["enabled"] = J.Bool(Settings()["enabled"]) ?? true,
        ["hasSocketToken"] = _secrets.Has(SocketSecret),
        ["hasAccessToken"] = _secrets.Has(AccessSecret),
        ["connected"] = _connected,
        ["error"] = _error,
        ["rules"] = Rules.DeepClone(),
    };

    public void Start()
    {
        lock (_gate)
            if (_loop is null || _loop.IsCompleted) _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        PublishSaved();
        var backoff = 2;
        while (!ct.IsCancellationRequested)
        {
            var token = _secrets.Get(SocketSecret);
            if (token is null || J.Bool(Settings()["enabled"]) == false)
            {
                _connected = false;
                _live.Publish("streamlabs.conn", LiveService.V(token is null ? L.T("Token mancante") : L.T("Disattivato"), state: false));
                return; // Update() starts it again
            }
            var started = DateTimeOffset.Now;
            _connCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try { await RunAsync(token, _connCts.Token); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (OperationCanceledException) { backoff = 1; }
            catch (Exception ex) when (ex is WebSocketException or IOException) { _error = L.T("Connessione a Streamlabs persa, riconnessione…"); }
            catch (ActionException ex) { _error = ex.Message; }
            catch (Exception ex)
            {
                _error = ex.Message;
                Log.Error("Streamlabs socket failed", ex);
            }
            _connected = false;
            _live.Publish("streamlabs.conn", LiveService.V(L.T("Non connesso"), state: false, sub: _error));
            if (DateTimeOffset.Now - started > TimeSpan.FromMinutes(2)) backoff = 2;
            try { await Task.Delay(TimeSpan.FromSeconds(backoff), ct); }
            catch (OperationCanceledException) { return; }
            backoff = Math.Min(backoff * 2, 120);
        }
    }

    /// <summary>Engine.IO v3 / socket.io v2 over a plain WebSocket: "0{open}", "40", "2"/"3" ping-pong, "42[event,data]".</summary>
    private async Task RunAsync(string token, CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"{SocketBase}/socket.io/?token={Uri.EscapeDataString(token)}&EIO=3&transport=websocket"), ct);
        var pingInterval = 25000;
        var pingTimeout = 60000;
        using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task? pinger = null;
        try
        {
            while (true)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(pingInterval + pingTimeout);
                var msg = await ReceiveAsync(ws, timeout.Token) ?? throw new IOException("closed");
                if (msg.Length == 0) continue;
                switch (msg[0])
                {
                    case '0': // open
                        var open = JsonNode.Parse(msg[1..]) as JsonObject;
                        pingInterval = J.Int(open?["pingInterval"]) ?? pingInterval;
                        pingTimeout = J.Int(open?["pingTimeout"]) ?? pingTimeout;
                        pinger = PingLoopAsync(ws, pingInterval, pingCts.Token);
                        break;
                    case '4':
                        if (msg.StartsWith("40", StringComparison.Ordinal))
                        {
                            _connected = true;
                            _error = null;
                            _live.Publish("streamlabs.conn", LiveService.V(L.T("Connesso"), state: true));
                        }
                        else if (msg.StartsWith("44", StringComparison.Ordinal))
                            throw new ActionException(L.T("Streamlabs ha rifiutato il Socket API Token"));
                        else if (msg.StartsWith("42", StringComparison.Ordinal) && JsonNode.Parse(msg[2..]) is JsonArray arr
                                 && J.Str(arr[0]) == "event" && arr.Count > 1 && arr[1] is JsonObject data)
                        {
                            try { OnEvent(data); }
                            catch (Exception ex) { Log.Error("Streamlabs event failed", ex); }
                        }
                        break;
                    case '1': throw new IOException("closed by server");
                }
            }
        }
        finally
        {
            pingCts.Cancel();
            if (pinger is not null) try { await pinger; } catch { }
        }
    }

    private static async Task PingLoopAsync(ClientWebSocket ws, int interval, CancellationToken ct)
    {
        var ping = Encoding.UTF8.GetBytes("2");
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            await Task.Delay(interval, ct);
            await ws.SendAsync(ping, WebSocketMessageType.Text, true, ct);
        }
    }

    private static async Task<string?> ReceiveAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        WebSocketReceiveResult r;
        do
        {
            r = await ws.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, r.Count);
            if (ms.Length > 1024 * 1024) throw new IOException(L.T("Messaggio troppo grande"));
        } while (!r.EndOfMessage);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    // ---- Events -------------------------------------------------------------------------------

    private void OnEvent(JsonObject data)
    {
        var type = J.Str(data["type"]) ?? "";
        var platform = J.Str(data["for"]) ?? "streamlabs";
        var items = data["message"] as JsonArray ?? (data["message"] is JsonObject one ? [one.DeepClone()] : []);
        foreach (var m in items.OfType<JsonObject>())
        {
            var name = J.Str(m["name"]) ?? J.Str(m["from"]) ?? L.T("Anonimo");
            StreamEvent? ev = null;
            switch (type, platform)
            {
                case ("donation", _):
                {
                    var amount = Num(m["amount"]);
                    var currency = J.Str(m["currency"]);
                    var formatted = J.Str(m["formatted_amount"]) ?? J.Str(m["formattedAmount"]) ?? $"{amount:0.##} {currency}";
                    AddTotal(amount, currency);
                    PublishLast("donation", name, formatted);
                    ev = new("donation", name, amount, null, J.Str(m["message"]), $"{name}: {formatted}");
                    break;
                }
                case ("follow", _):
                    ev = new("follow", name, null, null, null, L.F("{0} ti segue", name));
                    break;
                case ("subscription", _):
                {
                    var months = Num(m["months"]);
                    ev = new("sub", name, months, null, J.Str(m["message"]), months > 1 ? $"{name}: {months:0} mesi" : L.F("{0} si è abbonato", name));
                    break;
                }
                case ("bits", _):
                {
                    var bits = Num(m["amount"]);
                    ev = new("cheer", name, bits, null, J.Str(m["message"]), $"{name}: {bits:0} bits");
                    break;
                }
                case ("raid" or "raids", _):
                {
                    var viewers = Num(m["raiders"] ?? m["viewers"] ?? m["amount"]);
                    ev = new("raid", name, viewers, null, null, L.F("Raid di {0} ({1:0})", name, viewers));
                    break;
                }
                case ("superchat", _):
                {
                    // YouTube amounts come in micros; displayString is the formatted value.
                    var micros = Num(m["amount"]);
                    var amount = micros >= 10000 ? micros / 1_000_000 : micros;
                    var formatted = J.Str(m["displayString"]) ?? $"{amount:0.##} {J.Str(m["currency"])}";
                    PublishLast("donation", name, formatted);
                    ev = new("superchat", name, amount, null, J.Str(m["comment"]) ?? J.Str(m["message"]), $"{name}: {formatted}");
                    break;
                }
            }
            if (ev is null) continue;
            PublishLast("event", ev.Text, platform switch { "twitch_account" => "Twitch", "youtube_account" => "YouTube", _ => "Streamlabs" });
            Event?.Invoke(ev);
        }
    }

    private static double Num(JsonNode? n) =>
        J.Num(n) ?? (double.TryParse(J.Str(n), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0);

    private void AddTotal(double amount, string? currency)
    {
        lock (_gate)
        {
            _total += amount;
            _totalCurrency = currency ?? _totalCurrency;
        }
        PublishTotal();
    }

    public void ResetTotal()
    {
        lock (_gate) _total = 0;
        PublishTotal();
    }

    private void PublishTotal()
    {
        double t;
        string? c;
        lock (_gate) (t, c) = (_total, _totalCurrency);
        _live.Publish("streamlabs.total", LiveService.V($"{t.ToString("0.##", L.Culture)} {c}".Trim(), t));
    }

    private void PublishLast(string kind, string text, string? sub, bool save = true)
    {
        _live.Publish($"streamlabs.last:{kind}", LiveService.V(text, sub: sub));
        if (save) _config.UpdateSettings(s =>
        {
            var o = s["streamlabs"] as JsonObject ?? new JsonObject();
            var last = o["last"] as JsonObject ?? new JsonObject();
            last[kind] = new JsonObject { ["text"] = text, ["sub"] = sub };
            o["last"] = last;
            s["streamlabs"] = o;
        });
    }

    private void PublishSaved()
    {
        foreach (var k in new[] { "donation", "event" })
            if (Settings()["last"]?[k] is JsonObject v) PublishLast(k, J.Str(v["text"]) ?? "—", J.Str(v["sub"]), save: false);
            else _live.Publish($"streamlabs.last:{k}", LiveService.V("—"));
        PublishTotal();
    }

    // ---- REST ---------------------------------------------------------------------------------

    /// <summary>POST /alerts: a custom alert (type follow | subscription | donation | host) on the user's alert box.</summary>
    public async Task SendAlertAsync(JsonObject fields)
    {
        var token = _secrets.Get(AccessSecret) ?? throw new ActionException(L.T("Manca l'Access Token di Streamlabs (pannello del profilo → Streamlabs)"));
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/alerts")
        {
            Content = new StringContent(fields.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        HttpResponseMessage res;
        try { res = await Http.SendAsync(req); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new ActionException(L.T("Streamlabs non risponde: il PC è connesso a Internet?"));
        }
        using (res)
        {
            if (res.IsSuccessStatusCode) return;
            var text = await res.Content.ReadAsStringAsync();
            string? msg = null;
            try { msg = J.Str((JsonNode.Parse(text) as JsonObject)?["message"]); } catch { }
            throw new ActionException((int)res.StatusCode == 401
                ? L.T("Streamlabs: Access Token non valido")
                : $"Streamlabs: {msg ?? L.F("errore {0}", (int)res.StatusCode)}");
        }
    }

    /// <summary>"Prova connessione": the socket state, plus a check of the Access Token when present.</summary>
    public async Task<string> TestAsync()
    {
        var parts = new List<string>();
        parts.Add(_connected ? L.T("eventi collegati") : _secrets.Has(SocketSecret) ? L.F("eventi non collegati{0}", _error is null ? "" : $" ({_error})") : L.T("Socket API Token mancante"));
        if (_secrets.Get(AccessSecret) is { } token)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/user");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            try
            {
                using var res = await Http.SendAsync(req);
                parts.Add(res.IsSuccessStatusCode ? L.T("Access Token valido") : L.F("Access Token rifiutato ({0})", (int)res.StatusCode));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { parts.Add(L.T("API non raggiungibile")); }
        }
        return string.Join(" · ", parts);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _connCts?.Cancel();
    }
}
