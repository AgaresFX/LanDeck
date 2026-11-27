using System.Diagnostics;
using System.IO.Compression;
using PadNook.Core;

namespace PadNook.Platform;

/// <summary>
/// Downloads the official VB-CABLE driver pack from vb-audio.com and runs its setup (Windows asks for
/// administrator rights). The user stays on their own devices: the cable is only an extra pair of devices.
/// </summary>
internal static class CableInstaller
{
    private static readonly string[] Packs =
    [
        "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack46.zip",
        "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip",
        "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack43.zip",
    ];

    public const string HomePage = "https://vb-audio.com/Cable/";

    private static int _busy;
    public static bool Busy => Volatile.Read(ref _busy) == 1;

    /// <summary>Returns null when the setup was started, otherwise an error for the user.</summary>
    public static async Task<string?> InstallAsync()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return L.T("Installazione già in corso");
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "PadNook-VBCABLE-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            var zip = Path.Combine(dir, "vbcable.zip");

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("PadNook");
            var ok = false;
            foreach (var url in Packs)
            {
                try
                {
                    using var res = await http.GetAsync(url);
                    if (!res.IsSuccessStatusCode) continue;
                    await using (var f = File.Create(zip)) await res.Content.CopyToAsync(f);
                    ok = true;
                    break;
                }
                catch (Exception ex) { Log.Error($"VB-CABLE download failed: {url}", ex); }
            }
            if (!ok)
            {
                OpenHomePage();
                return L.T("Download non riuscito: ho aperto la pagina ufficiale di VB-CABLE, scaricalo da lì.");
            }

            var extract = Path.Combine(dir, "pack");
            ZipFile.ExtractToDirectory(zip, extract);
            var setupName = Environment.Is64BitOperatingSystem
                ? (System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64
                    ? "VBCABLE_Setup_ARM64.exe" : "VBCABLE_Setup_x64.exe")
                : "VBCABLE_Setup.exe";
            var setup = Directory.EnumerateFiles(extract, "*.exe", SearchOption.AllDirectories)
                .OrderByDescending(f => string.Equals(Path.GetFileName(f), setupName, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(f => Path.GetFileName(f).Contains("x64", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault(f => Path.GetFileName(f).StartsWith("VBCABLE_Setup", StringComparison.OrdinalIgnoreCase));
            if (setup is null)
            {
                OpenHomePage();
                return L.T("Programma di installazione non trovato: ho aperto la pagina ufficiale di VB-CABLE.");
            }

            try
            {
                using var p = Process.Start(new ProcessStartInfo(setup)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WorkingDirectory = Path.GetDirectoryName(setup)!,
                });
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return L.T("Installazione annullata (permessi di amministratore negati).");
            }
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("VB-CABLE install failed", ex);
            return L.F("Installazione non riuscita: {0}", ex.Message);
        }
        finally { Volatile.Write(ref _busy, 0); }
    }

    public static void OpenHomePage()
    {
        try { Process.Start(new ProcessStartInfo(HomePage) { UseShellExecute = true })?.Dispose(); } catch { }
    }
}
