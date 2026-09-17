using System.Net.WebSockets;

namespace Selo.Multiplayer;

public class User
{
    public required WebSocket WebSocket { get; init; }
    public required string Id { get; init; }
    public string? Username { get; set; }

    public string ChatUsername => Username ??= $"Anonymous {GetId(Id)}";

    /// <summary>
    ///     ValueTask so a send that completes inline — the usual case for a small text
    ///     frame — costs no Task.
    /// </summary>
    public ValueTask SendMessageAsync(ReadOnlyMemory<byte> bytes)
    {
        return WebSocket.State != WebSocketState.Open
            ? ValueTask.CompletedTask
            : WebSocket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    public Task SendAsync(Utf8MessageHandler handler)
    {
        return SendAsync(handler.Message);
    }

    public async Task SendAsync(Utf8Message message)
    {
        try
        {
            await SendMessageAsync(message.Memory);
        }
        finally
        {
            message.Dispose();
        }
    }

    private static string GetId(string id)
    {
        var index = id.IndexOf(':');
        return index == -1 ? id : id[..index];
    }
}