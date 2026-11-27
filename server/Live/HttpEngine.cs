using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PadNook.Actions;
using PadNook.Core;

namespace PadNook.Live;

public sealed record HttpResult(int Status, string Body, string? ContentType, long Ms);

/// <summary>
/// Sends the HTTP requests of the "http" action and of HTTP dynamic keys. Request spec (JSON):
/// { method, url, headers: [{name, value}], bodyType: none|json|text|form, body, timeoutSec }.
/// </summary>
public sealed class HttpEngine(Templating templating)
{
    public const int MaxResponseBytes = 1024 * 1024;

    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5,
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(10),
        // Accented text in headers (e.g. ntfy titles) instead of an "ASCII only" error.
        RequestHeaderEncodingSelector = (_, _) => Encoding.UTF8,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private static readonly HashSet<string> Methods = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD"];

    public async Task<HttpResult> SendAsync(JsonObject spec, CancellationToken ct = default)
    {
        var method = (J.Str(spec["method"]) ?? "GET").Trim().ToUpperInvariant();
        if (!Methods.Contains(method)) throw new ActionException(L.F("Metodo HTTP non valido: {0}", method));
        var rawUrl = (J.Str(spec["url"]) ?? "").Trim();
        if (rawUrl.Length == 0) throw new ActionException(L.T("URL mancante"));
        var url = templating.Fill(rawUrl, Escape.Url).Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ActionException(L.T("URL non valido: deve iniziare con http:// o https://"));
        var timeout = Math.Clamp(J.Num(spec["timeoutSec"]) ?? 10, 1, 60);

        using var req = new HttpRequestMessage(new HttpMethod(method), uri);
        req.Headers.UserAgent.ParseAdd($"PadNook/{Program.Version}");

        var bodyType = J.Str(spec["bodyType"]) ?? "none";
        if (method is not ("GET" or "HEAD") && bodyType != "none") req.Content = BuildBody(bodyType, J.Str(spec["body"]) ?? "");

        foreach (var h in spec["headers"] as JsonArray ?? [])
        {
            var name = (J.Str(h?["name"]) ?? "").Trim();
            if (name.Length == 0) continue;
            if (name.IndexOfAny([':', '\r', '\n', ' ']) >= 0) throw new ActionException(L.F("Nome di header non valido: {0}", name));
            var value = templating.Fill(J.Str(h?["value"]), Escape.Header);
            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                if (req.Content is not null && MediaTypeHeaderValue.TryParse(value, out var mt)) req.Content.Headers.ContentType = mt;
                continue;
            }
            if (!req.Headers.TryAddWithoutValidation(name, value)) req.Content?.Headers.TryAddWithoutValidation(name, value);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeout));
        var sw = Stopwatch.StartNew();
        try
        {
            using var res = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var body = await ReadLimitedAsync(res.Content, cts.Token);
            return new HttpResult((int)res.StatusCode, body, res.Content.Headers.ContentType?.MediaType, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ActionException(L.F("Nessuna risposta entro {0:0} s", timeout));
        }
        catch (HttpRequestException ex)
        {
            throw new ActionException(L.F("Connessione non riuscita: {0}", ex.InnerException?.Message ?? ex.Message));
        }
    }

    private HttpContent BuildBody(string bodyType, string body)
    {
        switch (bodyType)
        {
            case "json":
                var json = templating.Fill(body, Escape.Json);
                try { using var _ = JsonDocument.Parse(json); }
                catch (JsonException ex) { throw new ActionException(L.F("Il corpo non è un JSON valido ({0})", ex.Message.Split('.')[0])); }
                return new StringContent(json, Encoding.UTF8, "application/json");
            case "form":
                // One "key=value" per line (or separated by &).
                var pairs = body.Split(['\n', '&'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(p => p.Split('=', 2))
                    .Select(p => new KeyValuePair<string, string>(templating.Fill(p[0], Escape.None), p.Length > 1 ? templating.Fill(p[1], Escape.None) : ""));
                return new FormUrlEncodedContent(pairs);
            default:
                return new StringContent(templating.Fill(body, Escape.None), Encoding.UTF8, "text/plain");
        }
    }

    private static async Task<string> ReadLimitedAsync(HttpContent content, CancellationToken ct)
    {
        await using var s = await content.ReadAsStreamAsync(ct);
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        int n;
        while ((n = await s.ReadAsync(buffer, ct)) > 0)
        {
            ms.Write(buffer, 0, Math.Min(n, MaxResponseBytes - (int)ms.Length));
            if (ms.Length >= MaxResponseBytes) break;
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    // ---- Responses ----------------------------------------------------------------------------

    /// <summary>A short error detail from a response body (Telegram "description", HA "message", plain text).</summary>
    public static string ErrorDetail(HttpResult r)
    {
        string? detail = null;
        try
        {
            if (JsonNode.Parse(r.Body) is JsonObject o)
                detail = J.Str(o["description"]) ?? J.Str(o["message"]) ?? J.Str(o["error"]) ?? J.Str((o["error"] as JsonObject)?["message"]);
        }
        catch (JsonException) { detail = r.Body.Trim(); }
        if (string.IsNullOrWhiteSpace(detail)) return $"HTTP {r.Status}";
        detail = detail.ReplaceLineEndings(" ");
        return $"HTTP {r.Status}: {(detail.Length > 120 ? detail[..120] + "…" : detail)}";
    }

    /// <summary>
    /// Picks a value out of a response. Path like current.temperature, items[0].name or 0.state;
    /// empty path = the whole body (a JSON scalar or the text).
    /// </summary>
    public static JsonNode? Extract(string body, string? path, out bool found)
    {
        found = true;
        JsonNode? node;
        try { node = JsonNode.Parse(body); }
        catch (JsonException)
        {
            found = string.IsNullOrWhiteSpace(path);
            return found ? JsonValue.Create(body.Trim()) : null;
        }
        if (string.IsNullOrWhiteSpace(path)) return node;
        foreach (var seg in path.Replace("[", ".").Replace("]", "").Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            node = node switch
            {
                JsonObject o => o.TryGetPropertyValue(seg, out var v) ? v : Ci(o, seg),
                JsonArray a when int.TryParse(seg, out var i) => i >= 0 && i < a.Count ? a[i] : i < 0 && -i <= a.Count ? a[a.Count + i] : null,
                _ => null,
            };
            if (node is null) { found = false; return null; }
        }
        return node;
    }

    private static JsonNode? Ci(JsonObject o, string key) =>
        o.FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>Live value of an HTTP dynamic key: text/value from the extracted field, state, status code.</summary>
    public static JsonObject ToLive(HttpResult r, string? path)
    {
        var ok = r.Status is >= 200 and < 300;
        var node = Extract(r.Body, path, out var found);
        string text;
        double? value = null;
        bool? state = null;
        switch (node)
        {
            case null:
                text = found ? "null" : ok ? "—" : $"HTTP {r.Status}";
                break;
            case JsonValue v when v.TryGetValue<bool>(out var b):
                text = b ? "ON" : "OFF";
                value = b ? 1 : 0;
                state = b;
                break;
            case JsonValue v when J.Num(v) is { } d:
                text = d.ToString("0.##", L.Culture);
                value = d;
                break;
            case JsonValue v when J.Str(v) is { } s:
                text = s.Trim();
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) value = parsed;
                else if (text.Equals("on", StringComparison.OrdinalIgnoreCase) || text.Equals("true", StringComparison.OrdinalIgnoreCase)) state = true;
                else if (text.Equals("off", StringComparison.OrdinalIgnoreCase) || text.Equals("false", StringComparison.OrdinalIgnoreCase)) state = false;
                break;
            default:
                text = node.ToJsonString();
                break;
        }
        if (text.Length > 80) text = text[..80] + "…";
        var o = LiveService.V(text, value, state ?? ok, sub: $"HTTP {r.Status}");
        o["status"] = r.Status;
        o["ok"] = ok;
        return o;
    }

    public static JsonObject ErrorLive(string message)
    {
        var o = LiveService.V(L.T("Errore"), state: false, sub: message);
        o["status"] = 0;
        o["ok"] = false;
        return o;
    }
}
