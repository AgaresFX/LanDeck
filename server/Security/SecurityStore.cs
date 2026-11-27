using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PadNook.Core;

namespace PadNook.Security;

public sealed class DeviceRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public string? DefaultProfileId { get; set; }
    public DateTimeOffset PairedAt { get; set; }
    public DateTimeOffset LastSeen { get; set; }

    public DeviceRecord Clone() => (DeviceRecord)MemberwiseClone();
}

public sealed class PinRecord
{
    public string Hash { get; set; } = "";
    public string Salt { get; set; } = "";
    public int Iterations { get; set; } = 100_000;
}

internal sealed class SecurityData
{
    public PinRecord? Pin { get; set; }
    public List<DeviceRecord> Devices { get; set; } = new();
}

/// <summary>
/// Owns security.json: paired devices (only token hashes are stored) and the edit PIN.
/// Kept separate from config.json so profile exports never contain secrets.
/// </summary>
public sealed partial class SecurityStore : IDisposable
{
    private readonly AppPaths _paths;
    private readonly object _gate = new();
    private readonly object _ioGate = new();
    private readonly System.Threading.Timer _saveTimer;
    private SecurityData _data = new();
    private bool _dirty;

    public event Action? DevicesChanged;

    public SecurityStore(AppPaths paths)
    {
        _paths = paths;
        _saveTimer = new System.Threading.Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_paths.SecurityFile)) return;
            try
            {
                _data = JsonSerializer.Deserialize<SecurityData>(File.ReadAllText(_paths.SecurityFile), J.Indented) ?? new();
            }
            catch (Exception ex)
            {
                Log.Error("security.json is invalid, devices must be paired again", ex);
                _data = new();
            }
        }
    }

    // ---- Devices -------------------------------------------------------------------------

    public (DeviceRecord Device, string Token) CreateDevice(string name)
    {
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var now = DateTimeOffset.Now;
        var rec = new DeviceRecord
        {
            Id = "d_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant(),
            Name = SanitizeName(name),
            TokenHash = HashToken(token),
            PairedAt = now,
            LastSeen = now,
        };
        lock (_gate)
        {
            _data.Devices.Add(rec);
            MarkDirty();
        }
        Log.Info($"Device paired: {rec.Id} ({rec.Name})");
        DevicesChanged?.Invoke();
        return (rec.Clone(), token);
    }

    public DeviceRecord? Authenticate(string token)
    {
        var hash = HashToken(token);
        lock (_gate)
        {
            var rec = _data.Devices.FirstOrDefault(d =>
                CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(d.TokenHash), Encoding.ASCII.GetBytes(hash)));
            if (rec is null) return null;
            rec.LastSeen = DateTimeOffset.Now;
            MarkDirty();
            return rec.Clone();
        }
    }

    public IReadOnlyList<DeviceRecord> Devices()
    {
        lock (_gate) return _data.Devices.Select(d => d.Clone()).ToList();
    }

    public DeviceRecord? Get(string id)
    {
        lock (_gate) return _data.Devices.FirstOrDefault(d => d.Id == id)?.Clone();
    }

    public bool Revoke(string id)
    {
        bool removed;
        lock (_gate)
        {
            removed = _data.Devices.RemoveAll(d => d.Id == id) > 0;
            if (removed) MarkDirty();
        }
        if (removed)
        {
            Log.Info($"Device revoked: {id}");
            DevicesChanged?.Invoke();
        }
        return removed;
    }

    public DeviceRecord? Update(string id, string? name, bool setDefaultProfile, string? defaultProfileId)
    {
        DeviceRecord? result;
        lock (_gate)
        {
            var rec = _data.Devices.FirstOrDefault(d => d.Id == id);
            if (rec is null) return null;
            if (name is not null) rec.Name = SanitizeName(name);
            if (setDefaultProfile) rec.DefaultProfileId = string.IsNullOrWhiteSpace(defaultProfileId) ? null : defaultProfileId;
            MarkDirty();
            result = rec.Clone();
        }
        DevicesChanged?.Invoke();
        return result;
    }

    private static string SanitizeName(string? name)
    {
        var n = (name ?? "").Trim();
        if (n.Length > 40) n = n[..40];
        return n.Length == 0 ? L.T("Dispositivo") : n;
    }

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    // ---- PIN -----------------------------------------------------------------------------

    public bool HasPin { get { lock (_gate) return _data.Pin is not null; } }

    public static bool IsValidPin(string? pin) => pin is not null && PinRegex().IsMatch(pin);

    public bool VerifyPin(string pin)
    {
        PinRecord? rec;
        lock (_gate) rec = _data.Pin;
        if (rec is null) return false;
        var salt = Convert.FromBase64String(rec.Salt);
        var expected = Convert.FromBase64String(rec.Hash);
        var actual = Rfc2898DeriveBytes.Pbkdf2(pin, salt, rec.Iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public void SetPin(string pin)
    {
        if (!IsValidPin(pin)) throw new ArgumentException(L.T("Il PIN deve avere da 4 a 8 cifre"));
        var salt = RandomNumberGenerator.GetBytes(16);
        const int iterations = 100_000;
        var hash = Rfc2898DeriveBytes.Pbkdf2(pin, salt, iterations, HashAlgorithmName.SHA256, 32);
        lock (_gate)
        {
            _data.Pin = new PinRecord
            {
                Hash = Convert.ToBase64String(hash),
                Salt = Convert.ToBase64String(salt),
                Iterations = iterations,
            };
            MarkDirty();
        }
    }

    [GeneratedRegex(@"^\d{4,8}$")]
    private static partial Regex PinRegex();

    // ---- Persistence ---------------------------------------------------------------------

    private void MarkDirty()
    {
        _dirty = true;
        _saveTimer.Change(500, Timeout.Infinite);
    }

    public void Flush()
    {
        lock (_ioGate)
        {
            string json;
            lock (_gate)
            {
                if (!_dirty) return;
                json = JsonSerializer.Serialize(_data, J.Indented);
                _dirty = false;
            }
            try { J.WriteAtomic(_paths.SecurityFile, json); }
            catch (Exception ex)
            {
                Log.Error("Unable to save security.json", ex);
                lock (_gate) _dirty = true;
            }
        }
    }

    public void Dispose()
    {
        _saveTimer.Dispose();
        Flush();
    }
}
