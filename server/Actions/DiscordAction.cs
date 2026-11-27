using System.Text.Json.Nodes;
using PadNook.Live;

namespace PadNook.Actions;

/// <summary>Discord voice: mute, deafen, volumes, push-to-talk, join/leave a voice channel.</summary>
public sealed class DiscordAction(DiscordClient discord) : IActionHandler
{
    public string Type => "discord";

    public Task ExecuteAsync(JsonObject action, ActionContext ctx) => discord.RunAsync(action);
}
