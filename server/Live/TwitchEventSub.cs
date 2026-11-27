using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using PadNook.Actions;
using PadNook.Core;

namespace PadNook.Live;

/// <summary>A streaming event for the rules (Twitch, later Streamlabs): kind (follow, sub, gift, resub, raid, cheer, redeem, online, offline) and its variables.</summary>
public sealed record StreamEvent(string Kind, string User, double? Amount, string? Reward, string? Message, string Text);

/// <summary>
/// EventSub over WebSocket (wss://eventsub.wss.twitch.tv/ws): subscribes within 10 s of the welcome,
/// follows session_reconnect without losing subscriptions, reconnects with a backoff when dropped.
/// </summary>
public sealed class TwitchEventSub : IDisposable
{
    private static readonly string DefaultUrl = TwitchService.EventSubUrl;

    private readonly TwitchService _tw;
    private readonly LiveService _live;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private Task? _loop;
    private CancellationTokenSource? _connCts;
    private volatile bool _connected;
    private string? _error;
    private readonly List<string> _failed = [];

    /// <summary>Raised for every notification turned into a rule event.</summary>
    public event Action<StreamEvent>? Event;

    public TwitchEventSub(TwitchService tw, LiveService live)
    {
        _tw = tw;
        _live = live;
        tw.AuthChanged += () =>
        {
            _connCts?.Cancel(); // reconnect with the new user / stop after logout
            _ = EnsureRunningAsync();
        };
    }

    public JsonObject Status() => new()
    {
        ["connected"] = _connected,
        ["error"] = _error,
        ["failed"] = new JsonArray(_failed.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray()),
    };

    public Task EnsureRunningAsync()
    {
        lock (_gate)
        {
            if (_loop is null || _loop.IsCompleted) _loop = Task.Run(() => LoopAsync(_cts.Token));
            return Task.CompletedTask;
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var backoff = 2;
        while (!ct.IsCancellationRequested && _tw.LoggedIn)
        {
            var started = DateTimeOffset.Now;
            try { await RunAsync(DefaultUrl, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is WebSocketException or IOException) { _error = L.T("Connessione agli eventi Twitch persa, riconnessione…"); }
            catch (ActionException ex) { _error = ex.Message; }
            catch (Exception ex)
            {
                _error = ex.Message;
                Log.Error("Twitch EventSub failed", ex);
            }
            _connected = false;
            if (DateTimeOffset.Now - started > TimeSpan.FromMinutes(2)) backoff = 2;
            try { await Task.Delay(TimeSpan.FromSeconds(backoff), ct); }
            catch (OperationCanceledException) { break; }
            backoff = Math.Min(backoff * 2, 120);
        }
        _connected = false;
    }

    private async Task RunAsync(string url, CancellationToken appCt)
    {
        _connCts = CancellationTokenSource.CreateLinkedTokenSource(appCt);
        var ct = _connCts.Token;
        var ws = new ClientWebSocket();
        try
        {
            await ws.ConnectAsync(new Uri(url), ct);
            var subscribed = false;
            var keepalive = 30;
            while (true)
            {
                // No message (not even a keepalive) within the timeout: the connection is dead.
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(keepalive + 10));
                var msg = await ReceiveAsync(ws, timeout.Token) ?? throw new IOException("closed");
                var type = J.Str(msg["metadata"]?["message_type"]);
                var payload = msg["payload"] as JsonObject;
                switch (type)
                {
                    case "session_welcome":
                        keepalive = J.Int(payload?["session"]?["keepalive_timeout_seconds"]) ?? keepalive;
                        _connected = true;
                        _error = null;
                        if (!subscribed)
                        {
                            await SubscribeAllAsync(J.Str(payload?["session"]?["id"]) ?? throw new IOException("session id mancante"));
                            subscribed = true;
                        }
                        break;
                    case "session_reconnect":
                    {
                        // Open the new connection first; subscriptions carry over.
                        var next = J.Str(payload?["session"]?["reconnect_url"]) ?? DefaultUrl;
                        var newWs = new ClientWebSocket();
                        await newWs.ConnectAsync(new Uri(next), ct);
                        var welcome = await ReceiveAsync(newWs, ct);
                        if (J.Str(welcome?["metadata"]?["message_type"]) != "session_welcome") throw new IOException("reconnect failed");
                        try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
                        ws.Dispose();
                        ws = newWs;
                        break;
                    }
                    case "notification":
                        try { OnNotification(J.Str(msg["metadata"]?["subscription_type"]) ?? "", payload?["event"] as JsonObject ?? new JsonObject()); }
                        catch (Exception ex) { Log.Error("Twitch event handling failed", ex); }
                        break;
                    case "revocation":
                        var revoked = J.Str(payload?["subscription"]?["type"]);
                        _error = L.F("Twitch ha revocato l'evento {0} ({1})", revoked, J.Str(payload?["subscription"]?["status"]));
                        break;
                }
            }
        }
        finally
        {
            ws.Dispose();
            _connected = false;
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
            if (ms.Length > 1024 * 1024) throw new IOException(L.T("Messaggio troppo grande"));
        } while (!r.EndOfMessage);
        return JsonNode.Parse(Encoding.UTF8.GetString(ms.ToArray())) as JsonObject;
    }

    private async Task SubscribeAllAsync(string sessionId)
    {
        var id = _tw.RequireUser();
        JsonObject B() => new() { ["broadcaster_user_id"] = id };
        var subs = new (string Type, string Version, JsonObject Condition)[]
        {
            ("channel.follow", "2", new JsonObject { ["broadcaster_user_id"] = id, ["moderator_user_id"] = id }),
            ("channel.subscribe", "1", B()),
            ("channel.subscription.gift", "1", B()),
            ("channel.subscription.message", "1", B()),
            ("channel.raid", "1", new JsonObject { ["to_broadcaster_user_id"] = id }),
            ("channel.cheer", "1", B()),
            ("channel.channel_points_custom_reward_redemption.add", "1", B()),
            ("stream.online", "1", B()),
            ("stream.offline", "1", B()),
            ("channel.update", "2", B()),
            ("channel.chat_settings.update", "1", new JsonObject { ["broadcaster_user_id"] = id, ["user_id"] = id }),
        };
        _failed.Clear();
        // In parallel: Twitch closes the socket if nothing is subscribed within 10 s.
        await Task.WhenAll(subs.Select(async s =>
        {
            try
            {
                await _tw.HelixAsync(HttpMethod.Post, "eventsub/subscriptions", body: new JsonObject
                {
                    ["type"] = s.Type,
                    ["version"] = s.Version,
                    ["condition"] = s.Condition,
                    ["transport"] = new JsonObject { ["method"] = "websocket", ["session_id"] = sessionId },
                });
            }
            catch (ActionException ex)
            {
                // e.g. subs/cheers on a channel that is not affiliate, or a missing scope.
                lock (_failed) _failed.Add($"{s.Type}: {ex.Message}");
                Log.Info($"Twitch EventSub {s.Type} not subscribed: {ex.Message}");
            }
        }));
    }

    private void OnNotification(string type, JsonObject e)
    {
        string U(string prefix = "user") => J.Str(e[$"{prefix}_name"]) ?? J.Str(e[$"{prefix}_login"]) ?? L.T("Anonimo");
        static string Tier(string? t) => t switch { "2000" => "Tier 2", "3000" => "Tier 3", _ => "Tier 1" };
        StreamEvent? ev = null;
        switch (type)
        {
            case "channel.follow":
                _tw.PublishLast("follower", U(), null);
                ev = new("follow", U(), null, null, null, L.F("{0} ti segue", U()));
                break;
            case "channel.subscribe":
                if (J.Bool(e["is_gift"]) == true) break; // counted by the gift event
                _tw.PublishLast("sub", U(), Tier(J.Str(e["tier"])));
                ev = new("sub", U(), null, null, null, L.F("{0} si è abbonato ({1})", U(), Tier(J.Str(e["tier"]))));
                break;
            case "channel.subscription.gift":
            {
                var gifter = J.Bool(e["is_anonymous"]) == true ? L.T("Anonimo") : U();
                var total = J.Num(e["total"]) ?? 1;
                _tw.PublishLast("sub", gifter, $"regala {total:0} sub");
                ev = new("gift", gifter, total, null, null, $"{gifter} regala {total:0} sub");
                break;
            }
            case "channel.subscription.message":
            {
                var months = J.Num(e["cumulative_months"]) ?? 1;
                var text = J.Str(e["message"]?["text"]);
                _tw.PublishLast("sub", U(), $"{months:0} mesi");
                ev = new("resub", U(), months, null, text, $"{U()} rinnova ({months:0} mesi)");
                break;
            }
            case "channel.raid":
            {
                var viewers = J.Num(e["viewers"]) ?? 0;
                var from = U("from_broadcaster_user");
                _tw.PublishLast("raid", from, $"{viewers:0} spettatori");
                ev = new("raid", from, viewers, null, null, L.F("Raid di {0} con {1:0} spettatori", from, viewers));
                break;
            }
            case "channel.cheer":
            {
                var bits = J.Num(e["bits"]) ?? 0;
                var who = J.Bool(e["is_anonymous"]) == true ? L.T("Anonimo") : U();
                _tw.PublishLast("cheer", who, $"{bits:0} bits");
                ev = new("cheer", who, bits, null, J.Str(e["message"]), $"{who}: {bits:0} bits");
                break;
            }
            case "channel.channel_points_custom_reward_redemption.add":
            {
                var reward = J.Str(e["reward"]?["title"]) ?? "";
                _tw.PublishLast("redeem", U(), reward);
                ev = new("redeem", U(), J.Num(e["reward"]?["cost"]), reward, J.Str(e["user_input"]), $"{U()}: {reward}");
                break;
            }
            case "stream.online":
                _tw.PublishLive(true, J.Str(e["started_at"]));
                _tw.Kick();
                ev = new("online", U("broadcaster_user"), null, null, null, L.T("Diretta iniziata"));
                break;
            case "stream.offline":
                _tw.PublishLive(false, null);
                _tw.Kick();
                ev = new("offline", U("broadcaster_user"), null, null, null, L.T("Diretta terminata"));
                break;
            case "channel.update":
                _live.Publish("twitch.title", LiveService.V(J.Str(e["title"]) ?? "", sub: J.Str(e["category_name"])));
                break;
            case "channel.chat_settings.update":
                _tw.PublishChatSettings(e);
                break;
        }
        if (ev is not null) Event?.Invoke(ev);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _connCts?.Cancel();
    }
}
