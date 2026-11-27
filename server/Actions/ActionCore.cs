using System.Text.Json.Nodes;
using PadNook.Core;

namespace PadNook.Actions;

/// <summary>
/// A pluggable action. Add a new action type by implementing this interface and
/// registering it in <see cref="ActionCatalog"/>.
/// </summary>
public interface IActionHandler
{
    /// <summary>Value of the "type" field in the action JSON.</summary>
    string Type { get; }

    Task ExecuteAsync(JsonObject action, ActionContext ctx);
}

/// <param name="Depth">Nesting level (macros inside macros).</param>
public sealed record ActionContext(string DeviceId, ActionExecutor Executor, int Depth = 0);

/// <summary>An error with a user-facing (Italian) message.</summary>
public sealed class ActionException(string message) : Exception(message);

public static class ActionCatalog
{
    public static IEnumerable<IActionHandler> CreateBuiltins(Live.LiveStateStore state, Live.ObsClient obs, Plugins.PluginHost plugins,
        Live.HttpEngine http, Live.HttpPoller poller, Live.TwitchService twitch, Core.Templating templating,
        Live.SlobsClient slobs, Live.StreamlabsSocket streamlabs, Live.LightroomClient lightroom, Live.DiscordClient discord) =>
    [
        new HotkeyAction(),
        new LaunchAction(),
        new MediaAction(),
        new TextAction(),
        new CommandAction(),
        new MacroAction(),
        new MouseAction(),
        new DiscordAction(discord),
        new SoundAction(),
        new SystemAction(),
        new WindowAction(),
        new CounterAction(state),
        new TimerAction(state),
        new ObsAction(obs, slobs),
        new StreamlabsAction(streamlabs, templating),
        new LightroomAction(lightroom),
        new HttpAction(http, poller),
        new TwitchAction(twitch, templating),
        new PluginAction(plugins),
    ];
}

public sealed class ActionExecutor
{
    // Action types handled entirely by the webapp.
    private static readonly HashSet<string> ClientSide = new(StringComparer.OrdinalIgnoreCase) { "navigate" };

    private readonly Dictionary<string, IActionHandler> _handlers;

    public ActionExecutor(IEnumerable<IActionHandler> handlers)
    {
        _handlers = handlers.ToDictionary(h => h.Type, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Runs an action. Returns null on success or a user-facing error message.</summary>
    public async Task<string?> ExecuteAsync(JsonObject action, ActionContext ctx)
    {
        var type = J.Str(action["type"]);
        if (string.IsNullOrEmpty(type) || ClientSide.Contains(type)) return null;
        if (!_handlers.TryGetValue(type, out var handler)) return L.F("Azione non supportata: {0}", type);
        try
        {
            await handler.ExecuteAsync(action, ctx);
            return null;
        }
        catch (ActionException ex)
        {
            return ex.Message;
        }
        catch (Exception ex)
        {
            Log.Error($"Action '{type}' failed", ex);
            return ex.Message;
        }
    }
}
