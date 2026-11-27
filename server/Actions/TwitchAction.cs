using System.Globalization;
using System.Text.Json.Nodes;
using PadNook.Core;
using PadNook.Live;

namespace PadNook.Actions;

/// <summary>Twitch actions on the logged-in user's own channel (Helix).</summary>
public sealed class TwitchAction(TwitchService tw, Templating templating) : IActionHandler
{
    public string Type => "twitch";

    private static readonly HashSet<string> Colors = ["primary", "blue", "green", "orange", "purple"];

    public async Task ExecuteAsync(JsonObject a, ActionContext ctx)
    {
        var id = tw.RequireUser();
        string Text(string field, int max)
        {
            var t = templating.Fill(J.Str(a[field]), Escape.None).Trim();
            return t.Length > max ? t[..max] : t;
        }
        switch (J.Str(a["command"]))
        {
            case "marker":
                await tw.HelixAsync(HttpMethod.Post, "streams/markers", body: new JsonObject
                {
                    ["user_id"] = id,
                    ["description"] = Text("description", 140),
                });
                break;

            case "clip":
            {
                var r = await tw.HelixAsync(HttpMethod.Post, "clips", $"broadcaster_id={id}");
                var clipId = J.Str(r?["data"]?[0]?["id"]) ?? throw new ActionException(L.T("Twitch non ha creato la clip"));
                var url = $"https://clips.twitch.tv/{clipId}";
                SetClipboard(url);
                tw.PublishLast("clip", url, L.T("link copiato negli appunti"), save: false);
                _ = ConfirmClipAsync(clipId);
                break;
            }

            case "channel":
            {
                var body = new JsonObject();
                var title = Text("title", 140);
                if (title.Length > 0) body["title"] = title;
                if (J.Str(a["gameId"]) is { Length: > 0 } game) body["game_id"] = game;
                if (a["tags"] is JsonArray tags && tags.Count > 0)
                    body["tags"] = new JsonArray(tags.Select(t => (JsonNode?)JsonValue.Create((J.Str(t) ?? "").Replace(" ", ""))).Where(t => J.Str(t)!.Length > 0).Take(10).ToArray());
                if (body.Count == 0) throw new ActionException(L.T("Nessun titolo, categoria o tag da impostare"));
                await tw.HelixAsync(HttpMethod.Patch, $"channels", $"broadcaster_id={id}", body);
                break;
            }

            case "commercial":
            {
                var length = Math.Clamp(J.Int(a["length"]) ?? 30, 30, 180);
                var r = await tw.HelixAsync(HttpMethod.Post, "channels/commercial", body: new JsonObject { ["broadcaster_id"] = id, ["length"] = length });
                if (J.Int(r?["data"]?[0]?["retry_after"]) is { } retry && retry > 0)
                    tw.PublishLast("ad", $"{J.Int(r?["data"]?[0]?["length"]) ?? length} s", L.F("prossima tra {0} min", retry / 60), save: false);
                break;
            }

            case "chat":
            {
                var msg = Text("message", 500);
                if (msg.Length == 0) throw new ActionException(L.T("Messaggio vuoto"));
                var r = await tw.HelixAsync(HttpMethod.Post, "chat/messages", body: new JsonObject
                {
                    ["broadcaster_id"] = id,
                    ["sender_id"] = id,
                    ["message"] = msg,
                });
                var sent = r?["data"]?[0];
                if (J.Bool(sent?["is_sent"]) == false)
                    throw new ActionException(L.F("Messaggio non inviato: {0}", J.Str(sent?["drop_reason"]?["message"]) ?? L.T("bloccato da Twitch")));
                break;
            }

            case "announce":
            {
                var msg = Text("message", 500);
                if (msg.Length == 0) throw new ActionException(L.T("Annuncio vuoto"));
                var color = J.Str(a["color"]) is { } c && Colors.Contains(c) ? c : "primary";
                await tw.HelixAsync(HttpMethod.Post, "chat/announcements", $"broadcaster_id={id}&moderator_id={id}",
                    new JsonObject { ["message"] = msg, ["color"] = color });
                break;
            }

            case "shoutout":
            {
                var (to, _) = await tw.UserByLoginAsync(Text("channel", 100));
                await tw.HelixAsync(HttpMethod.Post, "chat/shoutouts", $"from_broadcaster_id={id}&to_broadcaster_id={to}&moderator_id={id}");
                break;
            }

            case "raid":
            {
                var (to, _) = await tw.UserByLoginAsync(Text("channel", 100));
                await tw.HelixAsync(HttpMethod.Post, "raids", $"from_broadcaster_id={id}&to_broadcaster_id={to}");
                break;
            }

            case "raidCancel":
                await tw.HelixAsync(HttpMethod.Delete, "raids", $"broadcaster_id={id}");
                break;

            case "chatMode":
                await ChatModeAsync(a, id);
                break;

            case "poll":
            {
                var choices = (a["choices"] as JsonArray ?? []).Select(c => templating.Fill(J.Str(c), Escape.None).Trim())
                    .Where(c => c.Length > 0).Select(c => c.Length > 25 ? c[..25] : c).ToList();
                if (choices.Count is < 2 or > 5) throw new ActionException(L.T("Il sondaggio richiede da 2 a 5 scelte"));
                var title = Text("title", 60);
                if (title.Length == 0) throw new ActionException(L.T("Domanda del sondaggio mancante"));
                await tw.HelixAsync(HttpMethod.Post, "polls", body: new JsonObject
                {
                    ["broadcaster_id"] = id,
                    ["title"] = title,
                    ["choices"] = new JsonArray(choices.Select(c => (JsonNode?)new JsonObject { ["title"] = c }).ToArray()),
                    ["duration"] = Math.Clamp(J.Int(a["durationSec"]) ?? 120, 15, 1800),
                });
                break;
            }

            case "pollEnd":
            {
                var poll = await FindActiveAsync("polls", id, "ACTIVE") ?? throw new ActionException(L.T("Nessun sondaggio in corso"));
                await tw.HelixAsync(HttpMethod.Patch, "polls", body: new JsonObject
                {
                    ["broadcaster_id"] = id,
                    ["id"] = J.Str(poll["id"]),
                    ["status"] = J.Bool(a["archive"]) == true ? "ARCHIVED" : "TERMINATED",
                });
                break;
            }

            case "prediction":
            {
                var outcomes = (a["outcomes"] as JsonArray ?? []).Select(c => templating.Fill(J.Str(c), Escape.None).Trim())
                    .Where(c => c.Length > 0).Select(c => c.Length > 25 ? c[..25] : c).ToList();
                if (outcomes.Count is < 2 or > 10) throw new ActionException(L.T("Il pronostico richiede da 2 a 10 esiti"));
                var title = Text("title", 45);
                if (title.Length == 0) throw new ActionException(L.T("Domanda del pronostico mancante"));
                await tw.HelixAsync(HttpMethod.Post, "predictions", body: new JsonObject
                {
                    ["broadcaster_id"] = id,
                    ["title"] = title,
                    ["outcomes"] = new JsonArray(outcomes.Select(c => (JsonNode?)new JsonObject { ["title"] = c }).ToArray()),
                    ["prediction_window"] = Math.Clamp(J.Int(a["windowSec"]) ?? 120, 1, 1800),
                });
                break;
            }

            case "predictionLock":
            case "predictionCancel":
            case "predictionResolve":
            {
                var cmd = J.Str(a["command"]);
                var pred = await FindActiveAsync("predictions", id, cmd == "predictionLock" ? "ACTIVE" : "ACTIVE", "LOCKED")
                           ?? throw new ActionException(L.T("Nessun pronostico in corso"));
                var body = new JsonObject
                {
                    ["broadcaster_id"] = id,
                    ["id"] = J.Str(pred["id"]),
                    ["status"] = cmd switch { "predictionLock" => "LOCKED", "predictionCancel" => "CANCELED", _ => "RESOLVED" },
                };
                if (cmd == "predictionResolve")
                {
                    var outcomes = pred["outcomes"] as JsonArray ?? [];
                    var n = J.Int(a["outcome"]) ?? 1;
                    if (n < 1 || n > outcomes.Count) throw new ActionException(L.F("Esito {0} inesistente: il pronostico ne ha {1}", n, outcomes.Count));
                    body["winning_outcome_id"] = J.Str(outcomes[n - 1]?["id"]);
                }
                await tw.HelixAsync(HttpMethod.Patch, "predictions", body: body);
                break;
            }

            default: throw new ActionException(L.T("Comando Twitch non valido"));
        }
        tw.Kick();
    }

    private async Task<JsonObject?> FindActiveAsync(string kind, string id, params string[] statuses)
    {
        var r = await tw.HelixAsync(HttpMethod.Get, kind, $"broadcaster_id={id}&first=5");
        return (r?["data"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(p => statuses.Contains(J.Str(p["status"])));
    }

    /// <summary>slow / follower / sub / emote: on, off or toggle (reads the real state first).</summary>
    private async Task ChatModeAsync(JsonObject a, string id)
    {
        var mode = J.Str(a["mode"]) ?? "slow";
        var field = mode switch
        {
            "slow" => "slow_mode",
            "follower" => "follower_mode",
            "sub" => "subscriber_mode",
            "emote" => "emote_mode",
            _ => throw new ActionException(L.T("Modalità chat non valida")),
        };
        var set = J.Str(a["set"]) ?? "toggle";
        bool on;
        if (set == "toggle") on = !(J.Bool((await tw.ChatSettingsAsync())[field]) ?? false);
        else on = set == "on";
        var body = new JsonObject { [field] = on };
        if (on && mode == "slow") body["slow_mode_wait_time"] = Math.Clamp(J.Int(a["seconds"]) ?? 30, 3, 120);
        if (on && mode == "follower") body["follower_mode_duration"] = Math.Clamp(J.Int(a["minutes"]) ?? 0, 0, 129600);
        var r = await tw.HelixAsync(HttpMethod.Patch, "chat/settings", $"broadcaster_id={id}&moderator_id={id}", body);
        if (r?["data"]?[0] is JsonObject s) tw.PublishChatSettings(s);
    }

    private async Task ConfirmClipAsync(string clipId)
    {
        // Clips are processed asynchronously: Get Clips returns it once ready (up to ~60 s).
        for (var i = 0; i < 12; i++)
        {
            await Task.Delay(5000);
            try
            {
                var r = await tw.HelixAsync(HttpMethod.Get, "clips", $"id={clipId}");
                if (r?["data"]?[0] is JsonObject c)
                {
                    tw.PublishLast("clip", J.Str(c["url"]) ?? $"https://clips.twitch.tv/{clipId}", L.T("clip pronta"), save: false);
                    return;
                }
            }
            catch (ActionException) { }
        }
        tw.PublishLast("clip", L.T("Clip non riuscita"), L.T("Twitch non l'ha completata"), save: false);
    }

    private static void SetClipboard(string text)
    {
        var t = new Thread(() =>
        {
            try { Clipboard.SetText(text); }
            catch (Exception ex) { Log.Info($"Clipboard busy: {ex.Message}"); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join(2000);
    }
}

/// <summary>
/// Event rules (settings.twitch.rules): when an event happens, flash a key on the devices and/or run an
/// action. Rule: { event, filter?, flashKeyId?, flashColor?, flashSec?, action? }.
/// </summary>
public sealed class EventRules(ActionExecutor executor, Func<string, string, int, Task> flash)
{
    public void Handle(StreamEvent ev, JsonArray rules)
    {
        foreach (var rule in rules.OfType<JsonObject>())
        {
            if (J.Str(rule["event"]) != ev.Kind || !FilterMatches(ev, J.Str(rule["filter"]))) continue;
            if (J.Str(rule["flashKeyId"]) is { Length: > 0 } keyId)
                _ = flash(keyId, J.Str(rule["flashColor"]) ?? "#9146ff", Math.Clamp((int)((J.Num(rule["flashSec"]) ?? 3) * 1000), 500, 30000));
            if (rule["action"] is JsonObject action) _ = RunAsync(action.DeepClone().AsObject(), ev);
        }
    }

    private async Task RunAsync(JsonObject action, StreamEvent ev)
    {
        // Event variables ({utente} {importo} {premio} {messaggio}) for the text of the action.
        Templating.EventVars.Value = new Dictionary<string, string>
        {
            ["utente"] = ev.User,
            ["importo"] = ev.Amount?.ToString("0.##", L.Culture) ?? "",
            ["premio"] = ev.Reward ?? "",
            ["messaggio"] = ev.Message ?? "",
        };
        var error = await executor.ExecuteAsync(action, new ActionContext("events", executor));
        if (error is not null) Log.Info($"Event rule '{ev.Kind}' action failed: {error}");
    }

    private static bool FilterMatches(StreamEvent ev, string? filter)
    {
        filter = filter?.Trim();
        if (string.IsNullOrEmpty(filter)) return true;
        return ev.Kind switch
        {
            "redeem" => (ev.Reward ?? "").Contains(filter, StringComparison.CurrentCultureIgnoreCase),
            "cheer" or "raid" or "gift" or "resub" or "donation" =>
                !double.TryParse(filter.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var min) || (ev.Amount ?? 0) >= min,
            _ => true,
        };
    }
}
