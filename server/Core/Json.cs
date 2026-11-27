using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PadNook.Core;

/// <summary>Small helpers to read loosely-typed JSON nodes safely.</summary>
public static class J
{
    public static readonly JsonSerializerOptions Indented = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string? Str(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static double? Num(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        return null;
    }

    public static int? Int(JsonNode? n) => Num(n) is { } d ? (int)Math.Round(d) : null;

    public static long? Long(JsonNode? n) => Num(n) is { } d ? (long)d : null;

    public static bool? Bool(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    /// <summary>Writes a file atomically (temp file + replace) to avoid corrupt configs.</summary>
    public static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(false));
        if (File.Exists(path))
        {
            try { File.Replace(tmp, path, path + ".bak", true); return; }
            catch (IOException) { /* fall back to move */ }
            catch (PlatformNotSupportedException) { }
        }
        File.Move(tmp, path, true);
    }
}
