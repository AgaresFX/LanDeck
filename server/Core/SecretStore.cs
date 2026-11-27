using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using PadNook.Config;

namespace PadNook.Core;

/// <summary>
/// Named credentials (tokens, webhook URLs, passwords) kept only on this PC, encrypted with DPAPI
/// (current Windows user) inside settings.secrets. Profiles reference them as {segreto:NAME}, so they
/// never reach the devices nor exported profiles. Names starting with "__" are internal (e.g. OBS).
/// </summary>
public sealed class SecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PadNook.Secrets.v1");
    private const string Prefix = "dpapi:";

    private readonly ConfigStore _config;

    public SecretStore(ConfigStore config)
    {
        _config = config;
        RemoveUnreadable();
    }

    /// <summary>
    /// Drops the entries this Windows user cannot decrypt (other user, config copied from another PC, older key):
    /// they would look saved in the editor while being unusable.
    /// </summary>
    private void RemoveUnreadable()
    {
        if (_config.SettingsSnapshot()["secrets"] is not JsonObject o) return;
        var bad = o.Where(kv => Decrypt(J.Str(kv.Value)) is null).Select(kv => kv.Key).ToList();
        if (bad.Count == 0) return;
        _config.UpdateSettings(s =>
        {
            if (s["secrets"] is JsonObject so) foreach (var k in bad) so.Remove(k);
        });
        _config.Flush();
        Log.Info($"Removed {bad.Count} saved secret(s) that cannot be decrypted: {string.Join(", ", bad)}");
    }

    private static string? Decrypt(string? stored)
    {
        if (stored is null || !stored.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        try
        {
            var plain = ProtectedData.Unprotect(Convert.FromBase64String(stored[Prefix.Length..]), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }

    public event Action? Changed;

    public static string CleanName(string? name)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) throw new ArgumentException(L.T("Nome della credenziale mancante"));
        if (n.Length > 40) throw new ArgumentException(L.T("Nome troppo lungo (max 40 caratteri)"));
        if (n.IndexOfAny(['{', '}', ':']) >= 0) throw new ArgumentException(L.T("Il nome non può contenere { } :"));
        return n;
    }

    /// <summary>Names visible to the editor (internal ones excluded).</summary>
    public List<string> Names()
    {
        var o = _config.SettingsSnapshot()["secrets"] as JsonObject;
        return o is null ? [] : o.Select(kv => kv.Key).Where(k => !k.StartsWith("__", StringComparison.Ordinal))
            .Order(StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public bool Has(string name) => Find(name) is not null;

    public string? Get(string name)
    {
        if (Find(name) is not { } stored) return null;
        var value = Decrypt(stored);
        // Another Windows user or a config copied from another PC: DPAPI cannot decrypt it.
        if (value is null) Log.Info($"Secret '{name}' cannot be decrypted");
        return value;
    }

    public void Set(string name, string value)
    {
        if (value.Length > 8192) throw new ArgumentException(L.T("Valore troppo lungo"));
        var enc = Prefix + Convert.ToBase64String(
            ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser));
        _config.UpdateSettings(s =>
        {
            var o = s["secrets"] as JsonObject ?? new JsonObject();
            // Same name with different case replaces the old entry.
            foreach (var k in o.Select(kv => kv.Key).Where(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)).ToList()) o.Remove(k);
            o[name] = enc;
            s["secrets"] = o;
        });
        _config.Flush();
        Changed?.Invoke();
    }

    public bool Delete(string name)
    {
        var removed = false;
        _config.UpdateSettings(s =>
        {
            if (s["secrets"] is not JsonObject o) return;
            foreach (var k in o.Select(kv => kv.Key).Where(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)).ToList())
                removed |= o.Remove(k);
        });
        _config.Flush();
        if (removed) Changed?.Invoke();
        return removed;
    }

    private string? Find(string name)
    {
        if (_config.SettingsSnapshot()["secrets"] is not JsonObject o) return null;
        foreach (var (k, v) in o)
            if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) return J.Str(v);
        return null;
    }
}
