using System.Security.Cryptography;
using System.Text.RegularExpressions;
using PadNook.Core;

namespace PadNook.Web;

/// <summary>
/// Content-addressed image files in %APPDATA%\PadNook\images (name = hash + extension).
/// SVG is not accepted: it could run scripts when served from this origin.
/// </summary>
public sealed partial class ImageStore(AppPaths paths)
{
    public const int MaxBytes = 8 * 1024 * 1024;

    [GeneratedRegex(@"^[a-f0-9]{32}\.(png|jpg|gif|webp)$")]
    private static partial Regex IdRegex();

    public static string? ContentType(string id) => Path.GetExtension(id) switch
    {
        ".png" => "image/png",
        ".jpg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => null,
    };

    /// <summary>Detects the format from the file signature; returns null if unsupported.</summary>
    private static string? DetectExtension(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "png";
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "jpg";
        if (b.Length >= 6 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F' && b[3] == '8') return "gif";
        if (b.Length >= 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F'
            && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return "webp";
        return null;
    }

    /// <summary>Saves the image (deduplicated) and returns its id, or null if the format is not supported.</summary>
    public string? Save(byte[] data)
    {
        var ext = DetectExtension(data);
        if (ext is null) return null;
        var id = Convert.ToHexString(SHA256.HashData(data))[..32].ToLowerInvariant() + "." + ext;
        var path = Path.Combine(paths.ImagesDir, id);
        if (!File.Exists(path))
        {
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, data);
            File.Move(tmp, path, true);
            Log.Info($"Image saved: {id} ({data.Length / 1024} KB)");
        }
        return id;
    }

    public string? PathFor(string id)
    {
        if (!IdRegex().IsMatch(id)) return null;
        var path = Path.Combine(paths.ImagesDir, id);
        return File.Exists(path) ? path : null;
    }
}
