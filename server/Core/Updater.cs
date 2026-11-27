using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace PadNook.Core;

public sealed record ReleaseInfo(string Version, string Notes, string ExeUrl, string ShaUrl, long Size, string PageUrl);

/// <summary>
/// Checks the latest GitHub release (at startup and every 6 hours). When its version differs from this
/// one, the tray offers the update; installing downloads PadNook.exe, verifies its SHA-256 (PadNook.exe.sha256
/// in the same release), swaps the exe in place and restarts. Same path = the firewall rule stays valid.
/// </summary>
public sealed class Updater : IDisposable
{
    public const string Repo = "AgaresFX/PadNook";
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    private static readonly HttpClient Http = CreateClient();
    private readonly System.Threading.Timer _timer;
    private readonly object _gate = new();
    private string? _notifiedVersion;

    /// <summary>PADNOOK_UPDATE_URL (testing only) replaces the GitHub API address.</summary>
    private static string LatestUrl =>
        Environment.GetEnvironmentVariable("PADNOOK_UPDATE_URL") is { Length: > 0 } u ? u : $"https://api.github.com/repos/{Repo}/releases/latest";

    public ReleaseInfo? Available { get; private set; }
    public DateTime? LastCheck { get; private set; }

    /// <summary>A new version was found (raised once per version, on a background thread).</summary>
    public event Action<ReleaseInfo>? UpdateAvailable;

    public Updater()
    {
        _timer = new System.Threading.Timer(_ => _ = CheckAsync(false), null, TimeSpan.FromSeconds(20), Interval);
    }

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"PadNook/{Program.Version}");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    /// <summary>Returns the available update, or null when up to date. Throws on network errors if manual.</summary>
    public async Task<ReleaseInfo?> CheckAsync(bool manual)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var json = JsonNode.Parse(await Http.GetStringAsync(LatestUrl, cts.Token)) as JsonObject;
            LastCheck = DateTime.Now;
            var info = Parse(json);
            ReleaseInfo? fire = null;
            lock (_gate)
            {
                Available = info;
                if (info is not null && _notifiedVersion != info.Version)
                {
                    _notifiedVersion = info.Version;
                    fire = info;
                }
            }
            if (fire is not null && !manual) UpdateAvailable?.Invoke(fire);
            return info;
        }
        catch (Exception ex) when (!manual)
        {
            Log.Info($"Update check failed: {ex.Message}");
            return null;
        }
    }

    private static ReleaseInfo? Parse(JsonObject? release)
    {
        if (release is null || J.Bool(release["draft"]) == true || J.Bool(release["prerelease"]) == true) return null;
        var tag = (J.Str(release["tag_name"]) ?? "").Trim().TrimStart('v', 'V');
        if (!Version.TryParse(tag, out var remote)) return null;
        var current = Version.Parse(Program.Version);
        // Any different version is offered (a newer one, or an older one if a bad release was withdrawn).
        if (Normalize(remote) == Normalize(current)) return null;

        string? exe = null, sha = null;
        long size = 0;
        foreach (var a in (release["assets"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var name = J.Str(a["name"]);
            var url = J.Str(a["browser_download_url"]);
            if (string.Equals(name, "PadNook.exe", StringComparison.OrdinalIgnoreCase)) { exe = url; size = J.Long(a["size"]) ?? 0; }
            else if (string.Equals(name, "PadNook.exe.sha256", StringComparison.OrdinalIgnoreCase)) sha = url;
        }
        if (exe is null || sha is null) return null; // releases without a checksum are never installed automatically
        return new ReleaseInfo(Normalize(remote).ToString(3), J.Str(release["body"]) ?? "", exe, sha, size, J.Str(release["html_url"]) ?? "");
    }

    private static Version Normalize(Version v) => new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));

    /// <summary>Downloads, verifies and swaps the exe. Returns the path of the new exe to start.</summary>
    public static async Task<string> DownloadAndSwapAsync(ReleaseInfo info, IProgress<int>? progress)
    {
        var exe = Environment.ProcessPath!;
        var newFile = exe + ".new";
        var oldFile = exe + ".old";

        var expected = (await Http.GetStringAsync(info.ShaUrl)).Trim().Split(' ', '\t', '\r', '\n')[0].ToLowerInvariant();
        if (expected.Length != 64) throw new InvalidDataException(L.T("Checksum della release non valido"));

        using (var resp = await Http.GetAsync(info.ExeUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? info.Size;
            await using var src = await resp.Content.ReadAsStreamAsync();
            await using var dst = File.Create(newFile);
            var buffer = new byte[1 << 16];
            long done = 0;
            int read, lastPct = -1;
            while ((read = await src.ReadAsync(buffer)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read));
                done += read;
                var pct = total > 0 ? (int)(done * 100 / total) : 0;
                if (pct != lastPct) progress?.Report(lastPct = pct);
            }
        }

        string actual;
        await using (var f = File.OpenRead(newFile)) actual = Convert.ToHexString(await SHA256.HashDataAsync(f)).ToLowerInvariant();
        if (actual != expected)
        {
            File.Delete(newFile);
            throw new InvalidDataException(L.T("Il file scaricato non corrisponde al checksum: aggiornamento annullato"));
        }

        // A running exe can be renamed (not overwritten): old -> .old, new -> exe.
        if (File.Exists(oldFile)) File.Delete(oldFile);
        File.Move(exe, oldFile);
        try { File.Move(newFile, exe); }
        catch
        {
            File.Move(oldFile, exe); // roll back
            throw;
        }
        Log.Info($"Updated exe to {info.Version}");
        return exe;
    }

    /// <summary>Starts the new exe; it waits for this instance to exit (single-instance mutex).</summary>
    public static void Restart(string exe, string version)
    {
        Process.Start(new ProcessStartInfo(exe, $"--restart --updated {version}")
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe),
        })?.Dispose();
    }

    /// <summary>If the new exe does not start, puts the previous one back.</summary>
    public static void RollBack()
    {
        var exe = Environment.ProcessPath!;
        try
        {
            if (!File.Exists(exe + ".old")) return;
            if (File.Exists(exe)) File.Move(exe, exe + ".new", true);
            File.Move(exe + ".old", exe);
        }
        catch (Exception ex) { Log.Error("Roll back failed", ex); }
    }

    public void Dispose() => _timer.Dispose();
}
