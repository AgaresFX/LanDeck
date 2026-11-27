using System.Text.Json.Nodes;
using PadNook.Core;
using PadNook.Live;
using PadNook.Platform;
using PadNook.Plugins;

namespace PadNook.Actions;

/// <summary>Soundboard: plays an audio file on the PC.</summary>
public sealed class SoundAction : IActionHandler
{
    public string Type => "sound";

    public Task ExecuteAsync(JsonObject action, ActionContext ctx)
    {
        if (J.Str(action["command"]) == "stopAll") return SoundBoard.StopAllAsync();
        return SoundBoard.PlayAsync(J.Str(action["file"]) ?? "", J.Int(action["volume"]) ?? 100, J.Str(action["mode"]) ?? "restart");
    }
}

/// <summary>Lock, sleep, shutdown, screenshots, virtual desktops, brightness.</summary>
public sealed class SystemAction : IActionHandler
{
    public string Type => "system";

    public async Task ExecuteAsync(JsonObject action, ActionContext ctx)
    {
        var delay = Math.Clamp(J.Int(action["delaySec"]) ?? 0, 0, 86400);
        var step = Math.Clamp(J.Int(action["step"]) ?? 10, 1, 100);
        switch (J.Str(action["command"]))
        {
            case "lock": SystemControl.Lock(); break;
            case "sleep": SystemControl.Sleep(false); break;
            case "hibernate": SystemControl.Sleep(true); break;
            case "shutdown": SystemControl.Shutdown($"/s /t {delay}"); break;
            case "restart": SystemControl.Shutdown($"/r /t {delay}"); break;
            case "cancelShutdown": SystemControl.Shutdown("/a"); break;
            case "logoff": SystemControl.LogOff(); break;
            case "screenshot":
                SystemControl.Screenshot(J.Str(action["area"]) ?? "all", J.Str(action["target"]) ?? "file", J.Str(action["folder"]));
                break;
            case "snip": await Win32Input.SendChordAsync(["Win", "Shift", "KeyS"]); break;
            case "desktopNext": await Win32Input.SendChordAsync(["Ctrl", "Win", "ArrowRight"]); break;
            case "desktopPrev": await Win32Input.SendChordAsync(["Ctrl", "Win", "ArrowLeft"]); break;
            case "desktopNew": await Win32Input.SendChordAsync(["Ctrl", "Win", "KeyD"]); break;
            case "desktopClose": await Win32Input.SendChordAsync(["Ctrl", "Win", "F4"]); break;
            case "taskView": await Win32Input.SendChordAsync(["Win", "Tab"]); break;
            case "showDesktop": await Win32Input.SendChordAsync(["Win", "KeyD"]); break;
            case "brightnessUp": await Task.Run(() => SystemControl.ChangeBrightness(step, null)); break;
            case "brightnessDown": await Task.Run(() => SystemControl.ChangeBrightness(-step, null)); break;
            case "brightnessSet":
                await Task.Run(() => SystemControl.ChangeBrightness(null, Math.Clamp(J.Int(action["level"]) ?? 50, 0, 100)));
                break;
            default: throw new ActionException(L.T("Comando di sistema non valido"));
        }
    }
}

/// <summary>Window management on the active window or on the window of an app.</summary>
public sealed class WindowAction : IActionHandler
{
    public string Type => "window";

    public Task ExecuteAsync(JsonObject action, ActionContext ctx)
    {
        WindowControl.Execute(J.Str(action["command"]) ?? "", J.Str(action["app"]), J.Str(action["launch"]), J.Str(action["region"]));
        return Task.CompletedTask;
    }
}

public sealed class CounterAction(LiveStateStore state) : IActionHandler
{
    public string Type => "counter";

    public Task ExecuteAsync(JsonObject action, ActionContext ctx)
    {
        state.Counter(J.Str(action["name"]) ?? "", J.Str(action["op"]) ?? "inc", J.Num(action["step"]) ?? 1, J.Num(action["value"]));
        return Task.CompletedTask;
    }
}

public sealed class TimerAction(LiveStateStore state) : IActionHandler
{
    public string Type => "timer";

    public Task ExecuteAsync(JsonObject action, ActionContext ctx)
    {
        state.Timer(J.Str(action["name"]) ?? "", J.Str(action["op"]) ?? "toggle", J.Str(action["mode"]), J.Num(action["durationSec"]),
            J.Bool(action["alertPc"]), J.Bool(action["alertPhone"]));
        return Task.CompletedTask;
    }
}

/// <summary>OBS Studio: streaming, recording, scenes, audio inputs.</summary>
public sealed class ObsAction(ObsClient obs, SlobsClient slobs) : IActionHandler
{
    public string Type => "obs";

    public async Task ExecuteAsync(JsonObject action, ActionContext ctx)
    {
        var scene = J.Str(action["scene"]) ?? "";
        var input = J.Str(action["input"]) ?? "";
        // Same action for OBS Studio and Streamlabs Desktop ("app": "slobs").
        if (J.Str(action["app"]) == "slobs")
        {
            await slobs.RunAsync(J.Str(action["command"]) ?? "", scene, input);
            return;
        }
        switch (J.Str(action["command"]))
        {
            case "streamToggle": await obs.RequestAsync("ToggleStream"); break;
            case "streamStart": await obs.RequestAsync("StartStream"); break;
            case "streamStop": await obs.RequestAsync("StopStream"); break;
            case "recordToggle": await obs.RequestAsync("ToggleRecord"); break;
            case "recordPause": await obs.RequestAsync("ToggleRecordPause"); break;
            case "replaySave": await obs.RequestAsync("SaveReplayBuffer"); break;
            case "replayToggle": await obs.RequestAsync("ToggleReplayBuffer"); break;
            case "virtualCam": await obs.RequestAsync("ToggleVirtualCam"); break;
            case "scene":
                if (scene.Length == 0) throw new ActionException(L.T("Nessuna scena scelta"));
                await obs.RequestAsync("SetCurrentProgramScene", new JsonObject { ["sceneName"] = scene });
                break;
            case "muteToggle":
                if (input.Length == 0) throw new ActionException(L.T("Nessuna sorgente audio scelta"));
                await obs.RequestAsync("ToggleInputMute", new JsonObject { ["inputName"] = input });
                break;
            default: throw new ActionException(L.T("Comando OBS non valido"));
        }
    }
}

public sealed class PluginAction(PluginHost host) : IActionHandler
{
    public string Type => "plugin";

    public Task ExecuteAsync(JsonObject action, ActionContext ctx) =>
        host.RunActionAsync(J.Str(action["plugin"]) ?? "", J.Str(action["action"]) ?? "", action["fields"] as JsonObject);
}
