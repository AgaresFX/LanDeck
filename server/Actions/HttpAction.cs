using System.Text.Json.Nodes;
using PadNook.Live;

namespace PadNook.Actions;

/// <summary>"Richiesta HTTP": webhooks, smart-home devices, any REST API. Fails on 4xx/5xx.</summary>
public sealed class HttpAction(HttpEngine engine, HttpPoller poller) : IActionHandler
{
    public string Type => "http";

    public async Task ExecuteAsync(JsonObject action, ActionContext ctx)
    {
        var r = await engine.SendAsync(action);
        // Keys that show the state of the same device (e.g. a Shelly switch) refresh right away.
        poller.Kick();
        if (r.Status >= 400) throw new ActionException(HttpEngine.ErrorDetail(r));
    }
}
