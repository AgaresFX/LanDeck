using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace PadNook.Web;

/// <summary>One connected client (browser tab / Home Screen app).</summary>
public sealed class WsSession
{
    private const int MaxMessageBytes = 8 * 1024 * 1024;

    private readonly WebSocket _ws;
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    public Guid Id { get; } = Guid.NewGuid();
    public string Remote { get; }
    public string? DeviceId { get; set; }
    public bool Edit { get; set; }
    public string? EditToken { get; set; }
    public int PinFailures { get; set; }
    public DateTimeOffset PinLockedUntil { get; set; }

    public WsSession(WebSocket ws, string remote)
    {
        _ws = ws;
        Remote = remote;
    }

    public async Task ReceiveLoopAsync(Func<JsonObject, Task> onMessage, CancellationToken ct)
    {
        var buffer = new byte[32 * 1024];
        using var ms = new MemoryStream();
        while (_ws.State == WebSocketState.Open)
        {
            ms.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await CloseAsync(WebSocketCloseStatus.NormalClosure, "bye");
                    return;
                }
                ms.Write(buffer, 0, result.Count);
                if (ms.Length > MaxMessageBytes)
                {
                    await CloseAsync(WebSocketCloseStatus.MessageTooBig, "too big");
                    return;
                }
            } while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text) continue;

            JsonObject? msg;
            try { msg = JsonNode.Parse(ms.GetBuffer().AsSpan(0, (int)ms.Length)) as JsonObject; }
            catch { continue; }
            if (msg is not null) await onMessage(msg);
        }
    }

    public Task SendAsync(JsonObject msg) => SendRawAsync(msg.ToJsonString());

    public async Task SendRawAsync(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendGate.WaitAsync();
        try
        {
            if (_ws.State == WebSocketState.Open)
                await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch { /* connection is going away; the receive loop will clean up */ }
        finally { _sendGate.Release(); }
    }

    public async Task CloseAsync(WebSocketCloseStatus status, string description)
    {
        try
        {
            if (_ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await _ws.CloseOutputAsync(status, description, CancellationToken.None);
        }
        catch { }
    }
}
