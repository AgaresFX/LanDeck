using System.Globalization;
using System.Net.Http;
using System.Text.Json.Nodes;
using PadNook.Core;

namespace PadNook.Live;

/// <summary>
/// Current weather from Open-Meteo (free, no API key). Outgoing HTTPS only: no firewall rule needed.
/// Source id: "weather:LAT,LON".
/// </summary>
public sealed class WeatherService
{
    private static readonly TimeSpan Refresh = TimeSpan.FromMinutes(15);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly LiveService _live;
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _lastFetch = new();
    private HashSet<string> _sources = new();
    private System.Threading.Timer? _timer;

    static WeatherService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd($"PadNook/{Program.Version}");
    }

    public WeatherService(LiveService live) => _live = live;

    public void SetReferenced(IEnumerable<string> sources)
    {
        lock (_gate)
        {
            _sources = sources.ToHashSet();
            if (_sources.Count > 0) _timer ??= new System.Threading.Timer(_ => _ = PollAsync(), null, TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(1));
        }
        _ = PollAsync();
    }

    private async Task PollAsync()
    {
        List<string> due;
        lock (_gate)
        {
            due = _sources.Where(s => !_lastFetch.TryGetValue(s, out var t) || DateTime.UtcNow - t > Refresh).ToList();
            foreach (var s in due) _lastFetch[s] = DateTime.UtcNow;
        }
        foreach (var source in due)
        {
            try { await FetchAsync(source); }
            catch (Exception ex)
            {
                Log.Info($"Weather fetch failed for {source}: {ex.Message}");
                lock (_gate) _lastFetch[source] = DateTime.UtcNow - Refresh + TimeSpan.FromMinutes(2); // retry soon
            }
        }
    }

    private async Task FetchAsync(string source)
    {
        var parts = source[8..].Split(',');
        if (parts.Length != 2
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)) return;

        var url = string.Create(CultureInfo.InvariantCulture,
            $"https://api.open-meteo.com/v1/forecast?latitude={lat:0.####}&longitude={lon:0.####}&current=temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,is_day,wind_speed_10m&daily=temperature_2m_max,temperature_2m_min&forecast_days=1&timezone=auto");
        var json = JsonNode.Parse(await Http.GetStringAsync(url));
        var cur = json?["current"];
        if (cur is null) return;
        var temp = J.Num(cur["temperature_2m"]) ?? 0;
        var code = J.Int(cur["weather_code"]) ?? 0;
        var day = (J.Int(cur["is_day"]) ?? 1) == 1;
        var (desc, emoji) = Describe(code, day);
        var max = J.Num(json?["daily"]?["temperature_2m_max"]?[0]);
        var min = J.Num(json?["daily"]?["temperature_2m_min"]?[0]);
        var v = LiveService.V($"{temp:0}°", temp, sub: desc, icon: emoji);
        v["code"] = code;
        v["humidity"] = J.Num(cur["relative_humidity_2m"]);
        v["feels"] = J.Num(cur["apparent_temperature"]) is { } f ? Math.Round(f) : null;
        v["wind"] = J.Num(cur["wind_speed_10m"]) is { } w ? Math.Round(w) : null;
        if (max is not null && min is not null) v["range"] = $"{min:0}° / {max:0}°";
        _live.Publish(source, v);
    }

    /// <summary>Place search for the editor (Open-Meteo geocoding).</summary>
    public static async Task<JsonArray> SearchAsync(string query)
    {
        var url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(query)}&count=8&language=it&format=json";
        var json = JsonNode.Parse(await Http.GetStringAsync(url));
        var res = new JsonArray();
        foreach (var r in (json?["results"] as JsonArray ?? []).OfType<JsonObject>())
        {
            res.Add(new JsonObject
            {
                ["name"] = J.Str(r["name"]),
                ["region"] = string.Join(", ", new[] { J.Str(r["admin1"]), J.Str(r["country"]) }.Where(s => !string.IsNullOrEmpty(s))),
                ["lat"] = Math.Round(J.Num(r["latitude"]) ?? 0, 4),
                ["lon"] = Math.Round(J.Num(r["longitude"]) ?? 0, 4),
            });
        }
        return res;
    }

    private static (string, string) Describe(int code, bool day) => code switch
    {
        0 => (L.T("Sereno"), day ? "☀️" : "🌙"),
        1 => (L.T("Poco nuvoloso"), day ? "🌤️" : "🌙"),
        2 => (L.T("Parz. nuvoloso"), "⛅"),
        3 => (L.T("Coperto"), "☁️"),
        45 or 48 => (L.T("Nebbia"), "🌫️"),
        51 or 53 or 55 or 56 or 57 => (L.T("Pioviggine"), "🌦️"),
        61 or 63 or 65 or 66 or 67 => (L.T("Pioggia"), "🌧️"),
        71 or 73 or 75 or 77 => (L.T("Neve"), "🌨️"),
        80 or 81 or 82 => (L.T("Rovesci"), "🌦️"),
        85 or 86 => (L.T("Rovesci di neve"), "❄️"),
        95 or 96 or 99 => (L.T("Temporale"), "⛈️"),
        _ => ("—", "🌡️"),
    };
}
