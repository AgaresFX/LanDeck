using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using PadNook.Core;
using PadNook.Platform;

namespace PadNook.Actions;

/// <summary>Types a text into the focused window.</summary>
public sealed class TextAction : IActionHandler
{
    private const int MaxLength = 10_000;

    public string Type => "text";

    public Task ExecuteAsync(JsonObject action, ActionContext ctx)
    {
        var text = J.Str(action["text"]) ?? "";
        if (text.Length == 0) throw new ActionException(L.T("Nessun testo configurato"));
        if (text.Length > MaxLength) throw new ActionException(L.T("Testo troppo lungo (max 10.000 caratteri)"));
        return Win32Input.TypeTextAsync(text);
    }
}

/// <summary>Runs a cmd or PowerShell command, optionally without a window. Does not wait for it to finish.</summary>
public sealed class CommandAction : IActionHandler
{
    public string Type => "command";

    public Task ExecuteAsync(JsonObject action, ActionContext ctx)
    {
        var command = J.Str(action["command"])?.Trim() ?? "";
        if (command.Length == 0) throw new ActionException(L.T("Nessun comando configurato"));
        var hidden = J.Bool(action["hidden"]) ?? true;
        var shell = J.Str(action["shell"]) ?? "cmd";

        ProcessStartInfo psi;
        if (shell == "powershell")
        {
            // -EncodedCommand avoids any quoting problem with the user's script.
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
            var window = hidden ? "-WindowStyle Hidden" : "-NoExit";
            psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass {window} -EncodedCommand {encoded}");
        }
        else
        {
            // /k keeps a visible window open so the output can be read.
            psi = new ProcessStartInfo("cmd.exe", (hidden ? "/c " : "/k ") + command);
        }

        psi.UseShellExecute = false;
        psi.CreateNoWindow = hidden;
        var cwd = J.Str(action["cwd"]);
        psi.WorkingDirectory = !string.IsNullOrWhiteSpace(cwd)
            ? Environment.ExpandEnvironmentVariables(cwd)
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        try
        {
            if (!hidden) Win32Input.AllowForegroundForLaunchedApps();
            Process.Start(psi)?.Dispose();
        }
        catch (Win32Exception ex)
        {
            throw new ActionException(L.F("Impossibile eseguire il comando: {0}", ex.Message));
        }
        return Task.CompletedTask;
    }
}

/// <summary>Runs a sequence of actions with a delay after each step.</summary>
public sealed class MacroAction : IActionHandler
{
    private const int MaxDepth = 4;
    private const int MaxDelayMs = 60_000;

    public string Type => "macro";

    public async Task ExecuteAsync(JsonObject action, ActionContext ctx)
    {
        if (ctx.Depth >= MaxDepth) throw new ActionException(L.T("Macro annidate troppo in profondità"));
        var steps = (action["steps"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        if (steps.Count == 0) throw new ActionException(L.T("La macro non ha passi"));

        var inner = ctx with { Depth = ctx.Depth + 1 };
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i]["action"] is JsonObject step)
            {
                var error = await ctx.Executor.ExecuteAsync(step, inner);
                if (error is not null) throw new ActionException(L.F("Passo {0}: {1}", i + 1, error));
            }
            var delay = Math.Clamp(J.Int(steps[i]["delayMs"]) ?? 0, 0, MaxDelayMs);
            if (delay > 0) await Task.Delay(delay);
        }
    }
}
