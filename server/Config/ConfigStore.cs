using System.Text.Json.Nodes;
using PadNook.Core;

namespace PadNook.Config;

/// <summary>
/// Owns config.json. Profiles are kept as raw JSON (the webapp defines their exact shape,
/// see webapp/src/types.ts); the server only navigates them to resolve key actions.
/// </summary>
public sealed class ConfigStore : IDisposable
{
    public const int DefaultPort = 8765;

    private readonly AppPaths _paths;
    private readonly object _gate = new();
    private readonly object _ioGate = new();
    private readonly System.Threading.Timer _saveTimer;
    private JsonObject _root = new();
    private bool _dirty;

    public bool IsFirstRun { get; private set; }

    /// <summary>Raised (outside the lock) after a profile was added, replaced or deleted.</summary>
    public event Action? ProfilesChanged;

    public ConfigStore(AppPaths paths)
    {
        _paths = paths;
        _saveTimer = new System.Threading.Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Load()
    {
        lock (_gate)
        {
            if (File.Exists(_paths.ConfigFile))
            {
                try
                {
                    _root = JsonNode.Parse(File.ReadAllText(_paths.ConfigFile)) as JsonObject
                            ?? throw new InvalidDataException("Root is not an object");
                }
                catch (Exception ex)
                {
                    Log.Error("config.json is invalid, creating a new one", ex);
                    try { File.Copy(_paths.ConfigFile, _paths.ConfigFile + $".broken-{DateTime.Now:yyyyMMddHHmmss}", true); } catch { }
                    _root = DefaultConfig.Create();
                    _dirty = true;
                }
            }
            else
            {
                _root = DefaultConfig.Create();
                IsFirstRun = true;
                _dirty = true;
            }
            Normalize();
        }
        Flush();
    }

    private void Normalize()
    {
        if (_root["settings"] is not JsonObject) { _root["settings"] = new JsonObject(); _dirty = true; }
        if (_root["profiles"] is not JsonArray arr || arr.Count == 0)
        {
            _root["profiles"] = DefaultConfig.Create()["profiles"]!.DeepClone();
            _dirty = true;
        }
        if (_root["schemaVersion"] is null) { _root["schemaVersion"] = 1; _dirty = true; }
        if (_root["rev"] is null) { _root["rev"] = 1; _dirty = true; }
    }

    private long RevUnsafe => J.Long(_root["rev"]) ?? 1;

    public long Rev { get { lock (_gate) return RevUnsafe; } }

    public (long Rev, JsonArray Profiles) Snapshot()
    {
        lock (_gate) return (RevUnsafe, (JsonArray)_root["profiles"]!.DeepClone());
    }

    public JsonObject SettingsSnapshot()
    {
        lock (_gate) return (JsonObject)_root["settings"]!.DeepClone();
    }

    public void UpdateSettings(Action<JsonObject> mutate)
    {
        lock (_gate)
        {
            mutate((JsonObject)_root["settings"]!);
            MarkDirty();
        }
    }

    public int Port
    {
        get
        {
            lock (_gate)
            {
                var p = J.Int(_root["settings"]?["port"]);
                return p is >= 1024 and <= 65535 ? p.Value : DefaultPort;
            }
        }
    }

    public bool ProfileExists(string id)
    {
        lock (_gate) return IndexOf((JsonArray)_root["profiles"]!, id) >= 0;
    }

    public IReadOnlyList<(string Id, string Name)> ProfileNames()
    {
        lock (_gate)
        {
            return ((JsonArray)_root["profiles"]!)
                .OfType<JsonObject>()
                .Select(p => (J.Str(p["id"]) ?? "", J.Str(p["name"]) ?? L.T("Profilo")))
                .ToList();
        }
    }

    /// <summary>Inserts or replaces a whole profile. Returns the new revision.</summary>
    public long PutProfile(JsonObject profile)
    {
        var id = J.Str(profile["id"]);
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException(L.T("Profilo senza id"));
        if (profile["pages"] is not JsonArray pages || pages.Count == 0)
            throw new ArgumentException(L.T("Il profilo deve avere almeno una pagina"));

        long rev;
        lock (_gate)
        {
            var arr = (JsonArray)_root["profiles"]!;
            var copy = profile.DeepClone();
            var idx = IndexOf(arr, id);
            if (idx >= 0) arr[idx] = copy; else arr.Add(copy);
            rev = BumpRev();
        }
        ProfilesChanged?.Invoke();
        return rev;
    }

    public long DeleteProfile(string id)
    {
        long rev;
        lock (_gate)
        {
            var arr = (JsonArray)_root["profiles"]!;
            if (arr.Count <= 1) throw new InvalidOperationException(L.T("Non puoi eliminare l'ultimo profilo"));
            var idx = IndexOf(arr, id);
            if (idx < 0) throw new ArgumentException(L.T("Profilo non trovato"));
            arr.RemoveAt(idx);
            rev = BumpRev();
        }
        ProfilesChanged?.Invoke();
        return rev;
    }

    /// <summary>Returns a copy of a key definition, or null if it does not exist.</summary>
    public JsonObject? FindKey(string profileId, string pageId, string keyId)
    {
        lock (_gate)
        {
            var arr = (JsonArray)_root["profiles"]!;
            var idx = IndexOf(arr, profileId);
            if (idx < 0) return null;
            var page = (arr[idx]?["pages"] as JsonArray)?.OfType<JsonObject>()
                .FirstOrDefault(p => J.Str(p["id"]) == pageId);
            var key = (page?["keys"] as JsonArray)?.OfType<JsonObject>()
                .FirstOrDefault(k => J.Str(k["id"]) == keyId);
            return key?.DeepClone() as JsonObject;
        }
    }

    private static int IndexOf(JsonArray arr, string id)
    {
        for (var i = 0; i < arr.Count; i++)
            if (J.Str(arr[i]?["id"]) == id) return i;
        return -1;
    }

    private long BumpRev()
    {
        var rev = RevUnsafe + 1;
        _root["rev"] = rev;
        MarkDirty();
        return rev;
    }

    private void MarkDirty()
    {
        _dirty = true;
        _saveTimer.Change(400, Timeout.Infinite);
    }

    public void Flush()
    {
        lock (_ioGate)
        {
            string json;
            lock (_gate)
            {
                if (!_dirty) return;
                json = _root.ToJsonString(J.Indented);
                _dirty = false;
            }
            try
            {
                J.WriteAtomic(_paths.ConfigFile, json);
            }
            catch (Exception ex)
            {
                Log.Error("Unable to save config.json", ex);
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
