using System.Text.Json;
using System.Text.Json.Nodes;
using PadNook.Core;

namespace PadNook.Config;

/// <summary>ON/OFF state of toggle keys (state.json), shared by all devices.</summary>
public sealed class ToggleStore : IDisposable
{
    private readonly string _file;
    private readonly object _gate = new();
    private readonly System.Threading.Timer _saveTimer;
    private Dictionary<string, bool> _states = new();

    public ToggleStore(AppPaths paths)
    {
        _file = Path.Combine(paths.Root, "state.json");
        _saveTimer = new System.Threading.Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        try
        {
            if (File.Exists(_file))
                _states = JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(_file)) ?? new();
        }
        catch (Exception ex) { Log.Error("state.json is invalid, toggle states reset", ex); }
    }

    public bool Get(string keyId)
    {
        lock (_gate) return _states.TryGetValue(keyId, out var on) && on;
    }

    /// <summary>Flips the state and returns the new value.</summary>
    public bool Flip(string keyId)
    {
        lock (_gate)
        {
            var on = !(_states.TryGetValue(keyId, out var cur) && cur);
            SetUnsafe(keyId, on);
            return on;
        }
    }

    public void Set(string keyId, bool on)
    {
        lock (_gate) SetUnsafe(keyId, on);
    }

    private void SetUnsafe(string keyId, bool on)
    {
        if (on) _states[keyId] = true;
        else _states.Remove(keyId);
        _saveTimer.Change(500, Timeout.Infinite);
    }

    public JsonObject Snapshot()
    {
        lock (_gate)
        {
            var o = new JsonObject();
            foreach (var (k, v) in _states) o[k] = v;
            return o;
        }
    }

    public void Flush()
    {
        string json;
        lock (_gate) json = JsonSerializer.Serialize(_states);
        try { J.WriteAtomic(_file, json); }
        catch (Exception ex) { Log.Error("Unable to save state.json", ex); }
    }

    public void Dispose()
    {
        _saveTimer.Dispose();
        Flush();
    }
}
