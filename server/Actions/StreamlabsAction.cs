using System.Text.Json.Nodes;
using PadNook.Core;
using PadNook.Live;

namespace PadNook.Actions;

/// <summary>Streamlabs account: custom alert (POST /alerts) and reset of the session donation total.</summary>
public sealed class StreamlabsAction(StreamlabsSocket sl, Templating templating) : IActionHandler
{
    public string Type => "streamlabs";

    private static readonly HashSet<string> AlertTypes = ["follow", "subscription", "donation", "host"];

    public async Task ExecuteAsync(JsonObject a, ActionContext ctx)
    {
        switch (J.Str(a["command"]))
        {
            case "alert":
            {
                var type = J.Str(a["alertType"]) is { } t && AlertTypes.Contains(t) ? t : "donation";
                var body = new JsonObject { ["type"] = type };
                void Add(string field, string key)
                {
                    var v = templating.Fill(J.Str(a[field]), Escape.None).Trim();
                    if (v.Length > 0) body[key] = v;
                }
                Add("message", "message");
                Add("userMessage", "user_message");
                Add("imageHref", "image_href");
                Add("soundHref", "sound_href");
                Add("color", "special_text_color");
                if (J.Int(a["durationMs"]) is > 0 and var ms) body["duration"] = ms.ToString();
                await sl.SendAlertAsync(body);
                break;
            }
            case "resetTotal": sl.ResetTotal(); break;
            default: throw new ActionException(L.T("Comando Streamlabs non valido"));
        }
    }
}
