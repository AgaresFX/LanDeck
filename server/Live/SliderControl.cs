using System.Text.Json.Nodes;
using PadNook.Actions;
using PadNook.Core;
using PadNook.Platform;

namespace PadNook.Live;

/// <summary>
/// Slider keys: { target: vol | mic | app | brightness, device?, app? }. vol/mic without a device follow the
/// Windows default device; with one they control that device (devvol:ID). While a finger drags, values arrive
/// faster than some targets can apply them (DDC/CI brightness takes ~50 ms, the WMI fallback seconds), so each
/// target applies only the latest value and drops the ones in between.
/// </summary>
public sealed class SliderControl
{
    private readonly LiveService _live;
    private readonly object _gate = new();
    private readonly object _muteGate = new();
    private readonly Dictionary<string, (JsonObject Cfg, double Value, Action<string> OnError)> _pending = new();
    private readonly HashSet<string> _busy = new();

    private readonly LightroomClient? _lr;

    public SliderControl(LiveService live, LightroomClient? lr = null)
    {
        _live = live;
        _lr = lr;
    }

    private static string? Device(JsonObject cfg) => J.Str(cfg["device"]) is { Length: > 0 } d ? d : null;

    /// <summary>Live source that shows the slider's current value (also referenced by the config scan).</summary>
    public static string? SourceOf(JsonObject cfg) => (J.Str(cfg["target"]) ?? "vol") switch
    {
        "vol" or "mic" when Device(cfg) is { } dev => $"devvol:{dev}",
        "vol" => "vol",
        "mic" => "micvol",
        "app" => J.Str(cfg["app"]) is { Length: > 0 } app ? $"appvol:{app.Trim()}" : null,
        "brightness" => "brightness",
        "lightroom" => J.Str(cfg["param"]) is { Length: > 0 } p ? $"lr.pct:{p}" : null,
        _ => null,
    };

    /// <summary>Sets the target to value (0..100). Errors are reported through onError.</summary>
    public void Set(JsonObject cfg, double value, Action<string> onError)
    {
        var source = SourceOf(cfg) ?? throw new ArgumentException(L.T("Slider non configurato: scegli cosa controllare"));
        lock (_gate)
        {
            _pending[source] = (cfg, Math.Clamp(value, 0, 100), onError);
            if (!_busy.Add(source)) return; // the running worker picks up the new value
        }
        _ = Task.Run(() => Drain(source));
    }

    private void Drain(string source)
    {
        while (true)
        {
            (JsonObject Cfg, double Value, Action<string> OnError) job;
            lock (_gate)
            {
                if (!_pending.Remove(source, out job))
                {
                    _busy.Remove(source);
                    return;
                }
            }
            try { _live.Publish(source, Apply(job.Cfg, job.Value)); }
            catch (ActionException ex) { job.OnError(ex.Message); }
            catch (Exception ex)
            {
                Log.Error("Slider failed", ex);
                job.OnError(L.T("Impossibile applicare il valore"));
            }
        }
    }

    private JsonObject Apply(JsonObject cfg, double exact)
    {
        var target = J.Str(cfg["target"]) ?? "vol";
        if (target == "lightroom")
        {
            if (_lr is null) throw new ActionException(L.T("Lightroom non disponibile"));
            var result = _lr.SetPercentAsync(LightroomClient.CheckParam(J.Str(cfg["param"])), exact).GetAwaiter().GetResult();
            Thread.Sleep(40); // at most ~25 updates/s: Lightroom re-renders the photo at every change
            return result;
        }
        var value = (int)Math.Round(exact);
        var level = value / 100f;
        if (target is "vol" or "mic" && Device(cfg) is { } dev)
        {
            CoreAudio.SetDeviceVolume(dev, level);
            return DeviceValue(dev);
        }
        switch (target)
        {
            case "mic":
                CoreAudio.SetMicVolume(level);
                return CoreAudio.GetMicState() is { } mic ? LiveService.VolumeValue(mic) : LiveService.V($"{value}%", value);
            case "app":
                return LiveService.VolumeValue(CoreAudio.ChangeApp(J.Str(cfg["app"]) ?? "", set: level));
            case "brightness":
                return LiveService.BrightnessValue(SystemControl.ChangeBrightness(null, value));
            default:
                CoreAudio.SetVolume(level);
                return CoreAudio.GetMasterState() is { } master ? LiveService.VolumeValue(master) : LiveService.V($"{value}%", value);
        }
    }

    public static JsonObject DeviceValue(string id) =>
        CoreAudio.GetDeviceState(id) is { } st ? LiveService.VolumeValue(st) : LiveService.V("—", sub: L.T("dispositivo non collegato"));

    /// <summary>Tap on an audio slider: mute / unmute. Returns false for targets without mute.</summary>
    public bool ToggleMute(JsonObject cfg)
    {
        var source = SourceOf(cfg);
        if (source is null) return false;
        var target = J.Str(cfg["target"]) ?? "vol";
        // Fast taps run one after another: two toggles at once could read the same state and cancel out.
        lock (_muteGate)
        {
            if (target is "vol" or "mic" && Device(cfg) is { } dev)
            {
                CoreAudio.ToggleDeviceMute(dev);
                _live.Publish(source, DeviceValue(dev));
                return true;
            }
            switch (target)
            {
                case "vol":
                    CoreAudio.ToggleMute();
                    if (CoreAudio.GetMasterState() is { } m) _live.Publish(source, LiveService.VolumeValue(m));
                    return true;
                case "mic":
                    CoreAudio.SetMicMute(null);
                    if (CoreAudio.GetMicState() is { } mic) _live.Publish(source, LiveService.VolumeValue(mic));
                    return true;
                case "app":
                    _live.Publish(source, LiveService.VolumeValue(CoreAudio.ChangeApp(J.Str(cfg["app"]) ?? "", toggleMute: true)));
                    return true;
                case "lightroom":
                    // Tap on a Lightroom slider: back to the default value.
                    if (_lr is null) return false;
                    _lr.SendAsync($"reset\t{LightroomClient.CheckParam(J.Str(cfg["param"]))}").GetAwaiter().GetResult();
                    return true;
                default:
                    return false;
            }
        }
    }
}
