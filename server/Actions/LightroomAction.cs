using System.Text.Json.Nodes;
using PadNook.Core;
using PadNook.Live;

namespace PadNook.Actions;

/// <summary>Lightroom Classic commands through the PadNook plugin.</summary>
public sealed class LightroomAction(LightroomClient lr) : IActionHandler
{
    public string Type => "lightroom";

    private static readonly HashSet<string> Simple =
        ["resetAll", "autoTone", "copy", "paste", "sync", "beforeAfter", "undo", "redo", "next", "prev"];
    private static readonly HashSet<string> Labels = ["red", "yellow", "green", "blue", "purple", "none"];
    private static readonly HashSet<string> Modules = ["library", "develop", "map", "book", "slideshow", "print", "web"];

    public async Task ExecuteAsync(JsonObject a, ActionContext ctx)
    {
        var cmd = J.Str(a["command"]) ?? "";
        switch (cmd)
        {
            case "inc":
            case "dec":
            {
                var p = LightroomClient.CheckParam(J.Str(a["param"]));
                var step = Math.Abs(J.Num(a["step"]) ?? LightroomClient.Params[p].Step);
                await lr.SendAsync($"{cmd}\t{p}\t{LightroomClient.Num(step)}");
                break;
            }
            case "set":
            {
                var p = LightroomClient.CheckParam(J.Str(a["param"]));
                await lr.SendAsync($"set\t{p}\t{LightroomClient.Num(J.Num(a["value"]) ?? 0)}");
                break;
            }
            case "reset":
                await lr.SendAsync($"reset\t{LightroomClient.CheckParam(J.Str(a["param"]))}");
                break;
            case "preset":
            {
                var name = (J.Str(a["preset"]) ?? "").Trim();
                if (name.Length == 0) throw new ActionException(L.T("Nessun preset scelto"));
                await lr.SendAsync($"preset\t{name.Replace('\t', ' ')}");
                break;
            }
            case "rating":
                await lr.SendAsync($"rating\t{Math.Clamp(J.Int(a["rating"]) ?? 0, 0, 5)}");
                break;
            case "flag":
                await lr.SendAsync($"flag\t{(J.Str(a["flag"]) is "pick" or "reject" ? J.Str(a["flag"]) : "none")}");
                break;
            case "label":
                await lr.SendAsync($"label\t{(J.Str(a["label"]) is { } l && Labels.Contains(l) ? l : "none")}");
                break;
            case "module":
                await lr.SendAsync($"module\t{(J.Str(a["module"]) is { } m && Modules.Contains(m) ? m : "library")}");
                break;
            default:
                if (!Simple.Contains(cmd)) throw new ActionException(L.T("Comando Lightroom non valido"));
                await lr.SendAsync(cmd);
                break;
        }
    }
}
