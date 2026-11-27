using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing.Imaging;

namespace PadNook.Platform;

/// <summary>Icons of running apps (from their exe), as small PNG data URLs for the editor.</summary>
internal static class AppIcons
{
    private static readonly ConcurrentDictionary<string, string?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>process name (without .exe) -> data:image/png;base64,… or null.</summary>
    public static string? DataUrl(string processName) => Cache.GetOrAdd(processName, Load);

    private static string? Load(string name)
    {
        try
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                using (p)
                {
                    string? path;
                    try { path = p.MainModule?.FileName; }
                    catch { continue; } // protected / 32-64 bit mismatch
                    if (string.IsNullOrEmpty(path)) continue;
                    using var icon = Icon.ExtractAssociatedIcon(path);
                    if (icon is null) continue;
                    using var bmp = new Bitmap(icon.ToBitmap(), 32, 32);
                    using var ms = new MemoryStream();
                    bmp.Save(ms, ImageFormat.Png);
                    return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
                }
            }
        }
        catch { /* no icon */ }
        return null;
    }
}
