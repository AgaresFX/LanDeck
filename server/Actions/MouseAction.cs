using System.Text.Json.Nodes;
using PadNook.Core;
using PadNook.Platform;

namespace PadNook.Actions;

/// <summary>
/// Mouse: click / press / release (optionally at a screen point), move to a point, move by an offset, scroll.
/// Coordinates are physical screen pixels, the same the editor's "pick from cursor" reads.
/// </summary>
public sealed class MouseAction : IActionHandler
{
    private const int MaxCoord = 100_000;

    public string Type => "mouse";

    public async Task ExecuteAsync(JsonObject action, ActionContext ctx)
    {
        var op = J.Str(action["op"]) ?? "click";
        var button = J.Str(action["button"]) ?? "left";
        int? x = J.Int(action["x"]), y = J.Int(action["y"]);

        void MoveIfSet()
        {
            if (x is int px && y is int py) Win32Input.MoveCursor(Math.Clamp(px, -MaxCoord, MaxCoord), Math.Clamp(py, -MaxCoord, MaxCoord));
        }

        switch (op)
        {
            case "click":
                MoveIfSet();
                if (x is not null) await Task.Delay(20); // let the target see the hover first
                await Win32Input.MouseClickAsync(button, Math.Clamp(J.Int(action["count"]) ?? 1, 1, 3));
                break;
            case "down":
                MoveIfSet();
                Win32Input.MouseButton(button, true);
                break;
            case "up":
                MoveIfSet();
                Win32Input.MouseButton(button, false);
                break;
            case "move":
                if (x is null || y is null) throw new ActionException(L.T("Posizione del mouse non impostata"));
                MoveIfSet();
                break;
            case "moveBy":
            {
                var (cx, cy) = Win32Input.CursorPosition();
                Win32Input.MoveCursor(cx + Math.Clamp(J.Int(action["dx"]) ?? 0, -MaxCoord, MaxCoord), cy + Math.Clamp(J.Int(action["dy"]) ?? 0, -MaxCoord, MaxCoord));
                break;
            }
            case "scroll":
                MoveIfSet();
                var amount = Math.Clamp(J.Int(action["amount"]) ?? -3, -50, 50);
                if (amount == 0) throw new ActionException(L.T("Scorrimento di 0 scatti"));
                Win32Input.MouseWheel(amount, J.Bool(action["horizontal"]) ?? false);
                break;
            default:
                throw new ActionException(L.F("Operazione del mouse sconosciuta: {0}", op));
        }
    }
}
