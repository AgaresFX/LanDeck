using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using PadNook.Core;
using PadNook.Platform;

namespace PadNook.Actions;

/// <summary>Keyboard shortcut. keys = ["Ctrl","Shift","KeyS"] (modifiers + KeyboardEvent.code names).</summary>
public sealed class HotkeyAction : IActionHandler
{
    public string Type => "hotkey";

    public Task ExecuteAsync(JsonObject action, ActionContext ctx)
    {
        var keys = (action["keys"] as JsonArray)?
            .Select(J.Str)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Cast<string>()
            .ToList() ?? [];
        if (keys.Count == 0) throw new ActionException(L.T("Nessun tasto configurato"));
        return Win32Input.SendChordAsync(keys);
    }
}

/// <summary>Opens a program, file, folder or URL via the Windows shell.</summary>
public sealed class LaunchAction : IActionHandler
{
    public string Type => "launch";

    public Task ExecuteAsync(JsonObject action, ActionContext ctx)
    {
        var target = Environment.ExpandEnvironmentVariables(J.Str(action["target"]) ?? "").Trim().Trim('"');
        if (target.Length == 0) throw new ActionException(L.T("Nessun programma o indirizzo configurato"));

        // "ifOpen": new (default) | focus | close. The process to check is "app", or the launched .exe.
        var ifOpen = J.Str(action["ifOpen"]) ?? "new";
        if (ifOpen != "new")
        {
            var app = J.Str(action["app"]);
            if (string.IsNullOrWhiteSpace(app) && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) app = target;
            if (!string.IsNullOrWhiteSpace(app) && WindowControl.HandleAlreadyOpen(app, ifOpen)) return Task.CompletedTask;
        }

        var psi = new ProcessStartInfo(target) { UseShellExecute = true };
        var args = J.Str(action["args"]);
        if (!string.IsNullOrWhiteSpace(args)) psi.Arguments = Environment.ExpandEnvironmentVariables(args);
        var cwd = J.Str(action["cwd"]);
        if (!string.IsNullOrWhiteSpace(cwd))
            psi.WorkingDirectory = Environment.ExpandEnvironmentVariables(cwd);
        else if (Path.IsPathRooted(target) && File.Exists(target))
            psi.WorkingDirectory = Path.GetDirectoryName(target) ?? "";

        try
        {
            Win32Input.AllowForegroundForLaunchedApps();
            Process.Start(psi)?.Dispose();
        }
        catch (Win32Exception ex)
        {
            throw new ActionException(L.F("Impossibile aprire \"{0}\": {1}", target, ex.Message));
        }
        return Task.CompletedTask;
    }
}

/// <summary>Media keys, master/app volume, microphone and output device (Core Audio).</summary>
public sealed class MediaAction : IActionHandler
{
    public string Type => "media";

    public Task ExecuteAsync(JsonObject action, ActionContext ctx)
    {
        var step = (float)(Math.Clamp(J.Num(action["step"]) ?? 2, 1, 50) / 100.0);
        var level = (float)(Math.Clamp(J.Num(action["level"]) ?? 50, 0, 100) / 100.0);
        var app = J.Str(action["app"]) ?? "";
        switch (J.Str(action["command"]))
        {
            case "playPause": Win32Input.TapVirtualKey(Win32Input.VK_MEDIA_PLAY_PAUSE); break;
            case "next": Win32Input.TapVirtualKey(Win32Input.VK_MEDIA_NEXT_TRACK); break;
            case "prev": Win32Input.TapVirtualKey(Win32Input.VK_MEDIA_PREV_TRACK); break;
            case "stop": Win32Input.TapVirtualKey(Win32Input.VK_MEDIA_STOP); break;
            case "volUp": CoreAudio.StepVolume(step); break;
            case "volDown": CoreAudio.StepVolume(-step); break;
            case "volSet": CoreAudio.SetVolume(level); break;
            case "mute": CoreAudio.ToggleMute(); break;
            case "appVolUp": CoreAudio.ChangeApp(app, delta: step); break;
            case "appVolDown": CoreAudio.ChangeApp(app, delta: -step); break;
            case "appVolSet": CoreAudio.ChangeApp(app, set: level); break;
            case "appMute": CoreAudio.ChangeApp(app, toggleMute: true); break;
            case "micMute":
                CoreAudio.SetMicMute(J.Str(action["mode"]) switch { "on" => true, "off" => false, _ => null });
                break;
            case "output": SwitchOutput(action); break;
            default: throw new ActionException(L.T("Comando multimediale non valido"));
        }
        return Task.CompletedTask;
    }

    /// <summary>devices = [{id, name}]: one device = select it, more = cycle through them.</summary>
    private static void SwitchOutput(JsonObject action)
    {
        var wanted = (action["devices"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(d => (Id: J.Str(d["id"]) ?? "", Name: J.Str(d["name"]) ?? ""))
            .Where(d => d.Id.Length > 0).ToList();
        if (wanted.Count == 0) throw new ActionException(L.T("Nessun dispositivo audio scelto"));
        var available = CoreAudio.ListOutputs();
        // Match by id, or by name if the id changed (e.g. the device was plugged into another port).
        var present = wanted
            .Select(w => available.FirstOrDefault(a => a.Id == w.Id) ?? available.FirstOrDefault(a => a.Name == w.Name))
            .Where(a => a is not null).Cast<AudioDevice>().DistinctBy(a => a.Id).ToList();
        if (present.Count == 0) throw new ActionException(L.F("Dispositivo non collegato: {0}", wanted[0].Name));
        var current = present.FindIndex(a => a.IsDefault);
        var next = present[(current + 1) % present.Count];
        CoreAudio.SetDefaultOutput(next.Id);
    }
}
