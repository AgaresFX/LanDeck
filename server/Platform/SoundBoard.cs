using System.Text.Json.Nodes;
using PadNook.Actions;
using PadNook.Config;
using PadNook.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace PadNook.Platform;

/// <summary>
/// Soundpad engine. Sounds play on the default output (the user's headphones) and, when a virtual cable
/// is installed (VB-CABLE), also into the cable together with the real microphone, so apps like Discord
/// that use "CABLE Output" as their microphone hear both the voice and the sounds.
/// The Windows default devices are never changed.
/// </summary>
internal static class SoundBoard
{
    private static readonly object Gate = new();
    private static ConfigStore? _config;
    private static Settings _settings = new();

    private static Bus? _monitor; // default output
    private static Bus? _cable;   // virtual cable input
    private static MicSource? _mic;
    private static ISampleProvider? _micInput; // the mic as added to the cable mixer
    private static string? _micError;
    private static readonly List<Voice> Voices = new();
    private static System.Threading.Timer? _idleTimer;

    private sealed record Settings
    {
        public string Cable { get; init; } = "";   // render device id; "" = auto (VB-CABLE)
        public string Mic { get; init; } = "";     // capture device id; "" = Windows default (never a cable)
        public bool ToMic { get; init; } = true;   // send sounds into the cable
        public bool Passthrough { get; init; } = true; // forward the real mic into the cable
        public bool Monitor { get; init; } = true; // hear the sounds in the headphones
        public int MonitorVolume { get; init; } = 100;
        public int MicVolume { get; init; } = 100; // sound level in the mic
    }

    // ---- Setup --------------------------------------------------------------------------------

    public static void Init(ConfigStore config)
    {
        _config = config;
        _settings = Read(config.SettingsSnapshot()["soundpad"] as JsonObject);
        _idleTimer = new System.Threading.Timer(_ => IdleCheck(), null, 5000, 5000);
        Task.Run(() => { lock (Gate) ApplyRouting(); });
    }

    public static void Shutdown()
    {
        lock (Gate)
        {
            _idleTimer?.Dispose();
            StopMic();
            _monitor?.Dispose(); _monitor = null;
            _cable?.Dispose(); _cable = null;
        }
    }

    private static Settings Read(JsonObject? o) => o is null ? new Settings() : new Settings
    {
        Cable = J.Str(o["cable"]) ?? "",
        Mic = J.Str(o["mic"]) ?? "",
        ToMic = J.Bool(o["toMic"]) ?? true,
        Passthrough = J.Bool(o["passthrough"]) ?? true,
        Monitor = J.Bool(o["monitor"]) ?? true,
        MonitorVolume = Math.Clamp(J.Int(o["monitorVolume"]) ?? 100, 0, 100),
        MicVolume = Math.Clamp(J.Int(o["micVolume"]) ?? 100, 0, 100),
    };

    public static void UpdateSettings(JsonObject o)
    {
        var s = Read(o);
        _config?.UpdateSettings(root => root["soundpad"] = new JsonObject
        {
            ["cable"] = s.Cable, ["mic"] = s.Mic, ["toMic"] = s.ToMic, ["passthrough"] = s.Passthrough,
            ["monitor"] = s.Monitor, ["monitorVolume"] = s.MonitorVolume, ["micVolume"] = s.MicVolume,
        });
        lock (Gate)
        {
            _settings = s;
            ApplyRouting();
        }
    }

    /// <summary>Re-reads the devices (e.g. after installing the cable).</summary>
    public static void Refresh()
    {
        lock (Gate)
        {
            _cable?.Dispose(); _cable = null;
            StopMic();
            ApplyRouting();
        }
    }

    public static bool IsCableName(string name) =>
        name.Contains("CABLE", StringComparison.OrdinalIgnoreCase) || name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase)
        || name.Contains("VoiceMeeter", StringComparison.OrdinalIgnoreCase);

    private static MMDevice? FindCable()
    {
        using var en = new MMDeviceEnumerator();
        var all = en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
        if (_settings.Cable.Length > 0) return all.FirstOrDefault(d => d.ID == _settings.Cable);
        return all.FirstOrDefault(d => d.FriendlyName.StartsWith("CABLE Input", StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(d => d.FriendlyName.Contains("VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase));
    }

    private static MMDevice? FindMic()
    {
        using var en = new MMDeviceEnumerator();
        var all = en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
        if (_settings.Mic.Length > 0) return all.FirstOrDefault(d => d.ID == _settings.Mic);
        // Default communications mic, unless it is the cable itself (that would loop the cable into itself).
        try
        {
            var def = en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            if (!IsCableName(def.FriendlyName)) return def;
        }
        catch { /* no default mic */ }
        return all.FirstOrDefault(d => !IsCableName(d.FriendlyName));
    }

    /// <summary>Opens the cable bus and the mic passthrough according to the settings. Caller holds Gate.</summary>
    private static void ApplyRouting()
    {
        try
        {
            var wantCable = _settings.ToMic || _settings.Passthrough;
            var cableDev = wantCable ? FindCable() : null;
            if (cableDev is null)
            {
                StopMic();
                _cable?.Dispose(); _cable = null;
                return;
            }
            if (_cable is null || _cable.DeviceId != cableDev.ID || !_cable.Alive)
            {
                StopMic();
                _cable?.Dispose();
                _cable = new Bus(cableDev, keepAlive: true);
            }

            if (_settings.Passthrough)
            {
                var micDev = FindMic();
                if (micDev is null) { StopMic(); _micError = L.T("Nessun microfono trovato"); }
                else if (_mic is null || _mic.DeviceId != micDev.ID || !_mic.Alive)
                {
                    StopMic();
                    _mic = new MicSource(micDev);
                    _micInput = Adapt(_mic, _cable.Format);
                    _cable.Mixer.AddMixerInput(_micInput);
                    _micError = null;
                }
            }
            else StopMic();
        }
        catch (Exception ex)
        {
            _micError = ex.Message;
            Log.Error("Soundpad routing failed", ex);
        }
    }

    private static void StopMic()
    {
        if (_micInput is not null) _cable?.Mixer.RemoveMixerInput(_micInput);
        _micInput = null;
        _mic?.Dispose();
        _mic = null;
    }

    // ---- Status for the editor -----------------------------------------------------------------

    public static JsonObject Status()
    {
        lock (Gate)
        {
            var outputs = new JsonArray();
            var inputs = new JsonArray();
            string? cableName = null;
            try
            {
                using var en = new MMDeviceEnumerator();
                foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                    outputs.Add(new JsonObject { ["id"] = d.ID, ["name"] = d.FriendlyName, ["cable"] = IsCableName(d.FriendlyName) });
                foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                    inputs.Add(new JsonObject { ["id"] = d.ID, ["name"] = d.FriendlyName, ["cable"] = IsCableName(d.FriendlyName) });
                cableName = FindCable()?.FriendlyName;
            }
            catch (Exception ex) { Log.Error("Soundpad: unable to list devices", ex); }

            var s = _settings;
            return new JsonObject
            {
                ["settings"] = new JsonObject
                {
                    ["cable"] = s.Cable, ["mic"] = s.Mic, ["toMic"] = s.ToMic, ["passthrough"] = s.Passthrough,
                    ["monitor"] = s.Monitor, ["monitorVolume"] = s.MonitorVolume, ["micVolume"] = s.MicVolume,
                },
                ["cableName"] = cableName,
                ["cableActive"] = _cable?.Alive == true,
                ["micName"] = _mic?.Name,
                ["micActive"] = _mic?.Alive == true,
                ["micError"] = _micError,
                ["installing"] = CableInstaller.Busy,
                ["outputs"] = outputs,
                ["inputs"] = inputs,
            };
        }
    }

    // ---- Playback -----------------------------------------------------------------------------

    /// <param name="mode">restart = play again from the start; toggle = stop if playing; overlap = always a new copy.</param>
    public static Task PlayAsync(string file, int volume, string mode) => Task.Run(() =>
    {
        var path = Environment.ExpandEnvironmentVariables(file.Trim().Trim('"'));
        if (path.Length == 0) throw new ActionException(L.T("Nessun file audio configurato"));
        if (!File.Exists(path)) throw new ActionException(L.F("File audio non trovato: {0}", path));

        lock (Gate)
        {
            Voices.RemoveAll(v => v.Finished);
            var existing = Voices.Where(v => string.Equals(v.Path, path, StringComparison.OrdinalIgnoreCase)).ToList();
            if (mode != "overlap" && existing.Count > 0)
            {
                foreach (var v in existing) v.Stop();
                Voices.RemoveAll(v => v.Stopped);
                if (mode == "toggle") return;
            }

            var gain = Math.Clamp(volume, 0, 100) / 100f;
            var voice = new Voice(path);
            try
            {
                if (_settings.Monitor) EnsureMonitor();
                if (_settings.ToMic && (_cable is null || !_cable.Alive)) ApplyRouting();

                if (_settings.Monitor && _monitor is not null)
                    voice.Add(_monitor, gain * _settings.MonitorVolume / 100f);
                if (_settings.ToMic && _cable is not null && _cable.Alive && _cable.DeviceId != _monitor?.DeviceId)
                    voice.Add(_cable, gain * _settings.MicVolume / 100f);
            }
            catch (Exception ex) when (ex is not ActionException)
            {
                voice.Stop();
                Log.Error($"Unable to play {path}", ex);
                throw new ActionException(L.F("Impossibile riprodurre il file: {0}", ex.Message));
            }
            if (voice.Parts == 0) throw new ActionException(L.T("Nessun dispositivo audio su cui riprodurre il suono"));
            Voices.Add(voice);
        }
    });

    public static Task StopAllAsync() => Task.Run(() =>
    {
        lock (Gate)
        {
            foreach (var v in Voices) v.Stop();
            Voices.Clear();
        }
    });

    /// <summary>Opens (or reopens) the bus on the current default output. Caller holds Gate.</summary>
    private static void EnsureMonitor()
    {
        using var en = new MMDeviceEnumerator();
        MMDevice def;
        try { def = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); }
        catch { _monitor?.Dispose(); _monitor = null; return; }
        if (_monitor is not null && _monitor.DeviceId == def.ID && _monitor.Alive) return;
        _monitor?.Dispose();
        _monitor = new Bus(def, keepAlive: false);
    }

    /// <summary>Closes the headphone bus after a while without sounds, so the output is not kept busy.</summary>
    private static void IdleCheck()
    {
        lock (Gate)
        {
            Voices.RemoveAll(v => v.Finished);
            if (_monitor is { } m && (!m.Alive || (m.Mixer.MixerInputs.Count() == 0 && DateTime.UtcNow - m.LastUse > TimeSpan.FromSeconds(20))))
            {
                m.Dispose();
                _monitor = null;
            }
            // Cable unplugged / mic lost: try again.
            if ((_cable is not null && !_cable.Alive) || (_mic is not null && !_mic.Alive)) ApplyRouting();
        }
    }

    // ---- Building blocks ----------------------------------------------------------------------

    /// <summary>Converts a source to the format of a mixer (channels, then sample rate).</summary>
    private static ISampleProvider Adapt(ISampleProvider src, WaveFormat target)
    {
        ISampleProvider p = src.WaveFormat.Channels == target.Channels ? src : new ChannelAdapter(src, target.Channels);
        if (p.WaveFormat.SampleRate != target.SampleRate) p = new WdlResamplingSampleProvider(p, target.SampleRate);
        return p;
    }

    /// <summary>One output device with a mixer that always runs while it is open.</summary>
    private sealed class Bus : IDisposable
    {
        private readonly WasapiOut _out;
        public string DeviceId { get; }
        public MixingSampleProvider Mixer { get; }
        public WaveFormat Format => Mixer.WaveFormat;
        public bool Alive { get; private set; } = true;
        public DateTime LastUse { get; set; } = DateTime.UtcNow;

        public Bus(MMDevice device, bool keepAlive)
        {
            DeviceId = device.ID;
            var mix = device.AudioClient.MixFormat;
            Mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(mix.SampleRate, mix.Channels)) { ReadFully = true };
            _out = new WasapiOut(device, AudioClientShareMode.Shared, true, keepAlive ? 30 : 60);
            _out.PlaybackStopped += (_, e) =>
            {
                Alive = false;
                if (e.Exception is not null) Log.Error($"Soundpad output stopped ({device.FriendlyName})", e.Exception);
            };
            _out.Init(new SampleToWaveProvider(Mixer));
            _out.Play();
        }

        public void Dispose()
        {
            Alive = false;
            try { _out.Stop(); } catch { }
            try { _out.Dispose(); } catch { }
        }
    }

    /// <summary>A playing sound: one file reader per bus.</summary>
    private sealed class Voice(string path)
    {
        private readonly List<(Bus Bus, Part Part)> _parts = new();
        public string Path { get; } = path;
        public int Parts => _parts.Count;
        public bool Stopped { get; private set; }
        public bool Finished => Stopped || _parts.All(p => p.Part.Ended);

        public void Add(Bus bus, float gain)
        {
            var reader = new AudioFileReader(Path);
            var part = new Part(Adapt(reader, bus.Format), reader, gain);
            _parts.Add((bus, part));
            bus.LastUse = DateTime.UtcNow;
            bus.Mixer.AddMixerInput(part);
        }

        public void Stop()
        {
            Stopped = true;
            foreach (var (bus, part) in _parts)
            {
                bus.Mixer.RemoveMixerInput(part);
                bus.LastUse = DateTime.UtcNow;
                part.Dispose();
            }
        }
    }

    /// <summary>Sound source with volume; returns 0 samples once finished so the mixer drops it.</summary>
    private sealed class Part(ISampleProvider src, IDisposable reader, float gain) : ISampleProvider, IDisposable
    {
        private volatile bool _disposed;
        public bool Ended { get; private set; }
        public WaveFormat WaveFormat => src.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            if (_disposed || Ended) return 0;
            int n;
            try { n = src.Read(buffer, offset, count); }
            catch { n = 0; }
            if (n == 0) { Ended = true; Dispose(); return 0; }
            if (gain != 1f) for (var i = 0; i < n; i++) buffer[offset + i] *= gain;
            return n;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { reader.Dispose(); } catch { }
        }
    }

    /// <summary>Maps any channel count to another (mono is copied to all channels, otherwise channels wrap).</summary>
    private sealed class ChannelAdapter(ISampleProvider src, int outChannels) : ISampleProvider
    {
        private readonly int _in = src.WaveFormat.Channels;
        private float[] _buf = [];
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(src.WaveFormat.SampleRate, outChannels);

        public int Read(float[] buffer, int offset, int count)
        {
            var frames = count / outChannels;
            var need = frames * _in;
            if (_buf.Length < need) _buf = new float[need];
            var got = src.Read(_buf, 0, need) / _in;
            for (var f = 0; f < got; f++)
            {
                if (_in == 2 && outChannels == 1)
                {
                    buffer[offset + f] = (_buf[f * 2] + _buf[f * 2 + 1]) * 0.5f;
                    continue;
                }
                for (var c = 0; c < outChannels; c++)
                    buffer[offset + f * outChannels + c] = _buf[f * _in + (c % _in)];
            }
            return got * outChannels;
        }
    }
}
