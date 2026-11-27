using System.Security.Cryptography;

namespace PadNook.Security;

public enum PairResult { Ok, Invalid, RateLimited }

/// <summary>
/// Short-lived, single-use 6-digit pairing codes. A code is exchanged for a long-lived device token.
/// </summary>
public sealed class PairingService
{
    public static readonly TimeSpan TrayCodeTtl = TimeSpan.FromMinutes(5);
    private const int MaxFailuresPerMinute = 10;

    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _codes = new();
    private readonly Queue<DateTimeOffset> _failures = new();
    private string? _trayCode;

    // Development only: a fixed reusable code when PADNOOK_DEV=1 and PADNOOK_DEV_PAIRCODE are set.
    private static readonly string? DevCode =
        Environment.GetEnvironmentVariable("PADNOOK_DEV") == "1"
            ? Environment.GetEnvironmentVariable("PADNOOK_DEV_PAIRCODE")
            : null;

    /// <summary>Raised (from any thread) when the code shown in the tray window was used.</summary>
    public event Action? TrayCodeConsumed;

    /// <summary>The code shown in the tray window; regenerated when expired or when forced.</summary>
    public (string Code, DateTimeOffset Expires) GetTrayCode(bool forceNew = false)
    {
        lock (_gate)
        {
            Cleanup();
            if (!forceNew && _trayCode is not null && _codes.TryGetValue(_trayCode, out var exp))
                return (_trayCode, exp);
            if (_trayCode is not null) _codes.Remove(_trayCode);
            _trayCode = NewCodeUnsafe();
            var expires = DateTimeOffset.Now + TrayCodeTtl;
            _codes[_trayCode] = expires;
            return (_trayCode, expires);
        }
    }

    /// <summary>An extra code, e.g. for the browser editor or a device that wants to pair its Home Screen app.</summary>
    public (string Code, DateTimeOffset Expires) CreateCode(TimeSpan ttl)
    {
        lock (_gate)
        {
            Cleanup();
            var code = NewCodeUnsafe();
            var expires = DateTimeOffset.Now + ttl;
            _codes[code] = expires;
            return (code, expires);
        }
    }

    public PairResult TryConsume(string? code)
    {
        bool trayConsumed;
        lock (_gate)
        {
            Cleanup();
            if (_failures.Count >= MaxFailuresPerMinute) return PairResult.RateLimited;

            code = code?.Trim().Replace(" ", "");
            if (!string.IsNullOrEmpty(code) && code == DevCode) return PairResult.Ok;
            if (string.IsNullOrEmpty(code) || !_codes.Remove(code))
            {
                _failures.Enqueue(DateTimeOffset.Now);
                return PairResult.Invalid;
            }
            trayConsumed = code == _trayCode;
            if (trayConsumed) _trayCode = null;
        }
        if (trayConsumed) TrayCodeConsumed?.Invoke();
        return PairResult.Ok;
    }

    private string NewCodeUnsafe()
    {
        string code;
        do code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        while (_codes.ContainsKey(code));
        return code;
    }

    private void Cleanup()
    {
        var now = DateTimeOffset.Now;
        foreach (var k in _codes.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList())
            _codes.Remove(k);
        if (_trayCode is not null && !_codes.ContainsKey(_trayCode)) _trayCode = null;
        while (_failures.Count > 0 && now - _failures.Peek() > TimeSpan.FromMinutes(1))
            _failures.Dequeue();
    }
}
