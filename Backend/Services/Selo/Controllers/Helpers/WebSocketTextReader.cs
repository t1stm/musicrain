using System.Buffers;
using System.Net.WebSockets;
using System.Text;

namespace Selo.Controllers.Helpers;

/// <summary>
///     Reads whole text frames off one connection. Both buffers are rented once for the life of the
///     connection rather than per message, and the payload is decoded straight into chars — the
///     StringBuilder path allocated a string per frame plus one for the finished message.
/// </summary>
public sealed class WebSocketTextReader : IDisposable
{
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private char[] _chars = ArrayPool<char>.Shared.Rent(4096);
    private byte[] _receive = ArrayPool<byte>.Shared.Rent(4096);

    public void Dispose()
    {
        if (_receive.Length > 0) ArrayPool<byte>.Shared.Return(_receive);
        if (_chars.Length > 0) ArrayPool<char>.Shared.Return(_chars);

        _receive = [];
        _chars = [];
    }

    /// <returns>
    ///     The whole text message — valid only until the next read — or <c>null</c> when the socket
    ///     closed or faulted.
    /// </returns>
    public async Task<ReadOnlyMemory<char>?> ReadWholeMessageAsync(WebSocket webSocket,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (webSocket.State != WebSocketState.Open) return null;

            _decoder.Reset();
            var length = 0;
            ValueWebSocketReceiveResult receiveResult;

            do
            {
                receiveResult = await webSocket.ReceiveAsync(_receive.AsMemory(), cancellationToken);
                if (receiveResult.MessageType == WebSocketMessageType.Close) return null;
                if (receiveResult.MessageType != WebSocketMessageType.Text) continue;

                // a frame can end mid-codepoint, so the decoder carries the remainder over
                // rather than each frame being decoded on its own
                var room = Encoding.UTF8.GetMaxCharCount(receiveResult.Count);
                if (length + room > _chars.Length) GrowChars(length, length + room);

                length += _decoder.GetChars(_receive.AsSpan(0, receiveResult.Count), _chars.AsSpan(length),
                    receiveResult.EndOfMessage);
            } while (!receiveResult.EndOfMessage);

            return _chars.AsMemory(0, length);
        }
        // A connection that goes away is the end of the session, not a fault:
        // null is what the caller already treats as "this socket is done". Any
        // other exception is our own bug and has to stay visible — swallowing
        // everything turned a crash into a session that quietly stopped, with
        // the client left to guess from a socket that never says why.
        catch (Exception exception)
            when (exception
                      is OperationCanceledException
                      or WebSocketException
                      or ObjectDisposedException
                      or IOException)
        {
            return null;
        }
    }

    private void GrowChars(int length, int size)
    {
        var next = ArrayPool<char>.Shared.Rent(size);
        _chars.AsSpan(0, length).CopyTo(next);

        ArrayPool<char>.Shared.Return(_chars);
        _chars = next;
    }
}