using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using PadNook.Core;

namespace PadNook.Web;

/// <summary>
/// Online image search for key images: icons/logos from Iconify (api.iconify.design) and
/// Creative Commons photos from Wikimedia Commons and Openverse (api.openverse.org). None needs an API key.
/// Downloads go through <see cref="FetchAsync"/>, which only connects to public internet addresses.
/// </summary>
public static class ImageSearch
{
    public const int MaxDownloadBytes = 15 * 1024 * 1024;

    public sealed record Hit(string Thumb, string Url, string Title, string? Credit);

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = DecompressionMethods.All,
            // Every connection (redirects included) is checked here, so a result URL can never
            // reach the LAN or the PC itself, whatever its host name resolves to.
            ConnectCallback = async (ctx, ct) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
                var ip = addresses.FirstOrDefault(IsPublic)
                         ?? throw new HttpRequestException(L.F("Indirizzo non consentito: {0}", ctx.DnsEndPoint.Host));
                var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(ip, ctx.DnsEndPoint.Port), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        // Wikimedia asks API clients for a descriptive user agent with a contact URL.
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"PadNook/{Program.Version} (+https://github.com/AgaresFX/PadNook)");
        return c;
    }

    private static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (LanFilter.IsLocal(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return false;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] != 0 && b[0] < 224 && !(b[0] == 100 && b[1] >= 64 && b[1] <= 127); // no multicast/CGNAT
        }
        return !ip.IsIPv6Multicast;
    }

    // Colourful logo/emoji sets go first: they look best on a key.
    private const string ColorPrefixes = "logos,skill-icons,devicon,vscode-icons,flat-color-icons,fluent-emoji-flat,noto,twemoji";

    public static async Task<List<Hit>> SearchIconsAsync(string query, CancellationToken ct)
    {
        var q = Uri.EscapeDataString(query);
        var color = GetIconNamesAsync($"https://api.iconify.design/search?query={q}&limit=64&prefixes={ColorPrefixes}", ct);
        var all = GetIconNamesAsync($"https://api.iconify.design/search?query={q}&limit=128", ct);
        var names = (await color).Concat(await all).Distinct().Take(120).ToList();

        // The per-icon .svg endpoint is rate-limited (429 after a page of thumbnails), so the icon
        // data is fetched in one request per set and returned as data: URLs (thumb = final image).
        var sets = await Task.WhenAll(names.GroupBy(n => n[..n.IndexOf(':')]).Select(async g =>
        {
            var icons = string.Join(',', g.Select(n => n[(n.IndexOf(':') + 1)..]));
            try
            {
                return (Prefix: g.Key, Json: JsonNode.Parse(await Http.GetStringAsync(
                    $"https://api.iconify.design/{g.Key}.json?icons={Uri.EscapeDataString(icons)}", ct)));
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Log.Info($"Icon set {g.Key} failed: {ex.Message}");
                return (Prefix: g.Key, Json: (JsonNode?)null);
            }
        }));
        var byPrefix = sets.Where(s => s.Json is not null).ToDictionary(s => s.Prefix, s => s.Json!);

        var hits = new List<Hit>();
        foreach (var n in names)
        {
            var prefix = n[..n.IndexOf(':')];
            if (!byPrefix.TryGetValue(prefix, out var set)) continue;
            var svg = BuildSvg(set, n[(n.IndexOf(':') + 1)..]);
            if (svg is null) continue;
            var url = "data:image/svg+xml;base64," + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(svg));
            hits.Add(new Hit(url, url, n, null));
        }
        return hits;
    }

    /// <summary>Builds a standalone SVG from Iconify set JSON (aliases resolved to their parent icon).</summary>
    private static string? BuildSvg(JsonNode set, string name)
    {
        var icon = set["icons"]?[name];
        for (var i = 0; icon is null && i < 3; i++)
        {
            var parent = set["aliases"]?[name]?["parent"]?.GetValue<string>();
            if (parent is null) break;
            name = parent;
            icon = set["icons"]?[name];
        }
        var body = icon?["body"]?.GetValue<string>();
        if (body is null) return null;

        double Num(string key, double fallback) => icon?[key]?.GetValue<double>() ?? set[key]?.GetValue<double>() ?? fallback;
        var (left, top, w, h) = (Num("left", 0), Num("top", 0), Num("width", 16), Num("height", 16));
        var scale = 512 / Math.Max(w, h);
        string F(double v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);
        // Monochrome icons use currentColor: white reads well on dark keys.
        body = body.Replace("currentColor", "#ffffff");
        return $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(w * scale)}\" height=\"{F(h * scale)}\" " +
               $"viewBox=\"{F(left)} {F(top)} {F(w)} {F(h)}\">{body}</svg>";
    }

    private static async Task<IEnumerable<string>> GetIconNamesAsync(string url, CancellationToken ct)
    {
        try
        {
            var json = JsonNode.Parse(await Http.GetStringAsync(url, ct));
            return json?["icons"]?.AsArray().Select(x => x?.GetValue<string>()).OfType<string>()
                       .Where(s => s.Contains(':')).ToList() ?? [];
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Log.Info($"Icon search failed: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Both sources are queried together. Openverse is often very slow (15-35 s) for queries nobody searched
    /// recently, so it only gets 5 s: Wikimedia answers in about a second.
    /// </summary>
    public static async Task<List<Hit>> SearchPhotosAsync(string query, CancellationToken ct)
    {
        using var openverseCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        openverseCts.CancelAfter(TimeSpan.FromSeconds(5));
        var commons = SafePhotos("Wikimedia", SearchCommonsAsync(query, ct), ct);
        var openverse = SafePhotos("Openverse", SearchOpenverseAsync(query, openverseCts.Token), ct);
        var (a, b) = (await commons, await openverse);
        if (a is null && b is null) throw new HttpRequestException(L.T("Nessuna fonte di foto ha risposto"));

        // Alternate the two sources so the first results show some variety.
        var hits = new List<Hit>();
        var (la, lb) = (a ?? [], b ?? []);
        for (var i = 0; i < Math.Max(la.Count, lb.Count); i++)
        {
            if (i < la.Count) hits.Add(la[i]);
            if (i < lb.Count) hits.Add(lb[i]);
        }
        return hits;
    }

    private static async Task<List<Hit>?> SafePhotos(string source, Task<List<Hit>> task, CancellationToken ct)
    {
        try { return await task; }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Log.Info($"Photo search ({source}) failed: {ex.Message}");
            return null;
        }
    }

    private static async Task<List<Hit>> SearchOpenverseAsync(string query, CancellationToken ct)
    {
        var url = $"https://api.openverse.org/v1/images/?q={Uri.EscapeDataString(query)}&page_size=20"; // anonymous limit: more returns 401
        var json = JsonNode.Parse(await Http.GetStringAsync(url, ct));
        var hits = new List<Hit>();
        foreach (var r in json?["results"]?.AsArray() ?? [])
        {
            var full = r?["url"]?.GetValue<string>();
            var thumb = r?["thumbnail"]?.GetValue<string>();
            if (full is null || thumb is null) continue;
            var title = r?["title"]?.GetValue<string>() ?? "";
            var creator = r?["creator"]?.GetValue<string>();
            var license = r?["license"]?.GetValue<string>();
            var credit = creator is null ? null : $"{creator} · CC {license?.ToUpperInvariant()}";
            hits.Add(new Hit(thumb, full, title, credit));
        }
        return hits;
    }

    private static async Task<List<Hit>> SearchCommonsAsync(string query, CancellationToken ct)
    {
        // 960 px is plenty for a key and is one of the thumbnail sizes Wikimedia keeps ready (330 for the grid).
        var url = "https://commons.wikimedia.org/w/api.php?action=query&format=json&generator=search&gsrnamespace=6&gsrlimit=24" +
                  $"&gsrsearch={Uri.EscapeDataString(query + " filetype:bitmap")}" +
                  "&prop=imageinfo&iiprop=url|extmetadata&iiextmetadatafilter=Artist|LicenseShortName&iiurlwidth=960";
        var json = JsonNode.Parse(await Http.GetStringAsync(url, ct));
        var pages = json?["query"]?["pages"]?.AsObject().Select(p => p.Value).OfType<JsonNode>()
                        .OrderBy(p => p["index"]?.GetValue<int>() ?? int.MaxValue) ?? Enumerable.Empty<JsonNode>();
        var hits = new List<Hit>();
        foreach (var p in pages)
        {
            var info = p["imageinfo"]?[0];
            var full = info?["thumburl"]?.GetValue<string>();
            if (full is null) continue;
            var thumb = full.Contains("/960px-") ? full.Replace("/960px-", "/330px-") : full;
            var title = p["title"]?.GetValue<string>() ?? "";
            if (title.StartsWith("File:", StringComparison.Ordinal)) title = title[5..];
            title = Path.GetFileNameWithoutExtension(title);
            var artist = StripHtml(info?["extmetadata"]?["Artist"]?["value"]?.GetValue<string>());
            var license = info?["extmetadata"]?["LicenseShortName"]?["value"]?.GetValue<string>();
            var credit = string.IsNullOrWhiteSpace(artist) ? license : license is null ? artist : $"{artist} · {license}";
            hits.Add(new Hit(thumb, full, title, credit));
        }
        return hits;
    }

    private static string? StripHtml(string? html) =>
        html is null ? null : WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", "")).Trim();

    /// <summary>Downloads an image from the internet (https only, public hosts only, size-capped).</summary>
    public static async Task<(byte[] Data, string ContentType)> FetchAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException(L.T("URL non valido"));

        using var res = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        var type = res.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        if (!type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(L.T("Il link non è un'immagine"));
        if (res.Content.Headers.ContentLength > MaxDownloadBytes)
            throw new InvalidDataException(L.T("Immagine troppo grande"));

        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            ms.Write(buffer, 0, read);
            if (ms.Length > MaxDownloadBytes) throw new InvalidDataException(L.T("Immagine troppo grande"));
        }
        return (ms.ToArray(), type);
    }

    public static string ToJson(List<Hit> hits) => JsonSerializer.Serialize(
        hits.Select(h => new { thumb = h.Thumb, url = h.Url, title = h.Title, credit = h.Credit }));
}
