using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Selo.Controllers.Helpers;
using Selo.Multiplayer;

namespace Gaida.Tests;

public class Utf8MessageTests
{
    [Fact]
    public void AMessageGrowsPastItsRentalWithoutLosingWhatIsAlreadyWritten()
    {
        using var message = new Utf8Message(1);

        message.Write("current "u8);
        message.Write(41);
        message.Write("гайда"); // multi-byte, so max-byte-count and written-count differ
        message.Write(new string('x', 5000).AsSpan());

        Assert.Equal($"current 41гайда{new string('x', 5000)}", Encoding.UTF8.GetString(message.Memory.Span));
    }

    [Fact]
    public void FormattedValuesMatchWhatInterpolationUsedToProduce()
    {
        using var message = new Utf8Message();
        var handler = new Utf8MessageHandler(0, 0);

        message.Write(1800.5);
        message.Write(true);

        handler.AppendLiteral("chat ");
        handler.AppendFormatted("Ada".AsMemory());
        handler.AppendFormatted(0);

        Assert.Equal($"{1800.5}{true}", Encoding.UTF8.GetString(message.Memory.Span));
        Assert.Equal("chat Ada0", Encoding.UTF8.GetString(handler.Message.Memory.Span));
        handler.Message.Dispose();
    }

    [Fact]
    public void SerializingThroughTheBufferWriterKeepsTheRelaxedEncoder()
    {
        using var message = new Utf8Message(4);
        message.Write("queue "u8);

        using (var writer = new Utf8JsonWriter(message, CustomSerializer.WriterOptions))
        {
            JsonSerializer.Serialize(writer, new[] { "a+b", "&" });
        }

        // the strict encoder would have escaped these to + and &
        Assert.Equal("""queue ["a+b","&"]""", Encoding.UTF8.GetString(message.Memory.Span));
    }
}

public class WebSocketTextReaderFrameTests
{
    [Fact]
    public async Task AMessageSplitAcrossFramesIsReassembledEvenMidCodepoint()
    {
        var payload = "chat гайда"u8.ToArray();
        using var reader = new WebSocketTextReader();
        // one byte at a time puts a frame boundary inside every two-byte character
        var socket = new FramedWebSocket(payload, 1);

        var message = await reader.ReadWholeMessageAsync(socket);

        Assert.Equal("chat гайда", message!.Value.ToString());
    }

    [Fact]
    public async Task AMessageLongerThanTheRentalIsStillReturnedWhole()
    {
        var text = new string('y', 20_000);
        using var reader = new WebSocketTextReader();
        var socket = new FramedWebSocket(Encoding.UTF8.GetBytes(text), 4096);

        var message = await reader.ReadWholeMessageAsync(socket);

        Assert.Equal(text, message!.Value.ToString());
    }

    private sealed class FramedWebSocket(byte[] payload, int frameSize) : WebSocket
    {
        private int _offset;

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;

        public override void Abort()
        {
        }

        public override void Dispose()
        {
        }

        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken t)
        {
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken t)
        {
            return Task.CompletedTask;
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token)
        {
            throw new NotSupportedException();
        }

        public override ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer,
            CancellationToken cancellationToken)
        {
            var count = Math.Min(Math.Min(frameSize, buffer.Length), payload.Length - _offset);
            payload.AsSpan(_offset, count).CopyTo(buffer.Span);
            _offset += count;

            return new ValueTask<ValueWebSocketReceiveResult>(
                new ValueWebSocketReceiveResult(count, WebSocketMessageType.Text, _offset >= payload.Length));
        }

        public override Task SendAsync(ArraySegment<byte> b, WebSocketMessageType t, bool e, CancellationToken c)
        {
            return Task.CompletedTask;
        }

        public override ValueTask SendAsync(ReadOnlyMemory<byte> b, WebSocketMessageType t, bool e,
            CancellationToken c)
        {
            return ValueTask.CompletedTask;
        }
    }
}