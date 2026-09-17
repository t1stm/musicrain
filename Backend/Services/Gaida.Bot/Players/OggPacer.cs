using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;

namespace Gaida.Bot.Players;

/// <summary>
/// Reads the granule positions out of an Ogg/Opus byte stream, chunk by chunk, wherever the chunk
/// boundaries happen to fall. Granules count 48 kHz samples, so the highest one seen is exactly how
/// much audio has been handed over — which is what the feed loop paces against.
/// </summary>
/// <remarks>
/// Pre-skip is deliberately not subtracted: it is a few milliseconds, and DSharpPlus's own reader
/// does not apply it either (its <c>PreSkipSamples / 48000</c> is integer division and always
/// yields zero).
/// </remarks>
public sealed class OggGranuleScanner
{
    private enum State
    {
        Magic,
        Header,
        SegmentTable,
        Payload
    }

    private const int HeaderLength = 27;

    private readonly byte[] header = new byte[HeaderLength];
    private readonly byte[] segments = new byte[255];

    private State state = State.Magic;
    private int magicMatched;
    private int collected;
    private int segmentCount;
    private long payloadRemaining;

    /// <summary>The highest granule position seen so far, in 48 kHz samples.</summary>
    public long Granule { get; private set; }

    /// <summary>The audio handed over so far.</summary>
    public TimeSpan Fed => TimeSpan.FromSeconds(this.Granule / 48000.0);

    public void Scan(ReadOnlySpan<byte> data)
    {
        var index = 0;
        while (index < data.Length)
        {
            switch (this.state)
            {
                case State.Magic:
                    index = ScanForMagic(data, index);
                    break;

                case State.Header:
                {
                    // The magic is already matched and sitting in the buffer; fill the rest.
                    var take = Math.Min(HeaderLength - this.collected, data.Length - index);
                    data.Slice(index, take).CopyTo(this.header.AsSpan(this.collected));
                    this.collected += take;
                    index += take;

                    if (this.collected < HeaderLength) break;

                    var granule = BinaryPrimitives.ReadInt64LittleEndian(this.header.AsSpan(6, 8));

                    // -1 means no packet finishes on this page. A new file starts a new scanner,
                    // so within one stream the granule only ever climbs.
                    if (granule > this.Granule) this.Granule = granule;

                    this.segmentCount = this.header[26];
                    this.collected = 0;
                    this.state = this.segmentCount == 0 ? State.Magic : State.SegmentTable;
                    break;
                }

                case State.SegmentTable:
                {
                    var take = Math.Min(this.segmentCount - this.collected, data.Length - index);
                    data.Slice(index, take).CopyTo(this.segments.AsSpan(this.collected));
                    this.collected += take;
                    index += take;

                    if (this.collected < this.segmentCount) break;

                    this.payloadRemaining = 0;
                    for (var i = 0; i < this.segmentCount; i++) this.payloadRemaining += this.segments[i];

                    this.collected = 0;
                    this.state = this.payloadRemaining == 0 ? State.Magic : State.Payload;
                    break;
                }

                case State.Payload:
                {
                    var skip = (int)Math.Min(this.payloadRemaining, data.Length - index);
                    this.payloadRemaining -= skip;
                    index += skip;

                    if (this.payloadRemaining == 0) this.state = State.Magic;
                    break;
                }

                default:
                    throw new InvalidOperationException($"Unreachable scanner state {this.state}.");
            }
        }
    }

    /// <summary>Walks past the next <c>OggS</c>, tolerating a magic split across chunks.</summary>
    private int ScanForMagic(ReadOnlySpan<byte> data, int index)
    {
        ReadOnlySpan<byte> magic = "OggS"u8;

        while (index < data.Length)
        {
            var current = data[index++];

            if (current == magic[this.magicMatched])
            {
                this.magicMatched++;
                if (this.magicMatched < magic.Length) continue;

                magic.CopyTo(this.header);
                this.collected = magic.Length;
                this.magicMatched = 0;
                this.state = State.Header;
                return index;
            }

            // A mismatch can itself begin a fresh magic, as in "OOggS".
            this.magicMatched = current == magic[0] ? 1 : 0;
        }

        return index;
    }
}

/// <summary>Feeds one Ogg/Opus body into one audio writer, in real time.</summary>
public static class OggPacer
{
    /// <summary>
    /// How far ahead of playback the send queue is allowed to run. DSharpPlus's send queue is
    /// unbounded and cannot be cleared, so this is also the tail heard after a skip or a pause, and
    /// the cushion that covers a hiccup mid-track or a track change.
    /// </summary>
    public static readonly TimeSpan Lookahead = TimeSpan.FromSeconds(1);

    /// <param name="elapsed">How far playback itself has got — the player's clock, paused included.</param>
    /// <param name="waitWhilePaused">Returns once the player is playing again.</param>
    /// <param name="onProgress">Called after every chunk with the audio fed so far.</param>
    public static async Task FeedAsync
    (
        Stream source,
        PipeWriter writer,
        OggGranuleScanner scanner,
        Func<TimeSpan> elapsed,
        Func<CancellationToken, Task> waitWhilePaused,
        Func<TimeSpan, Task>? onProgress,
        CancellationToken ct
    )
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16384);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await waitWhilePaused(ct);

                var read = await source.ReadAsync(buffer, ct);
                if (read == 0) break;

                scanner.Scan(buffer.AsSpan(0, read));
                await writer.WriteAsync(buffer.AsMemory(0, read), ct);

                if (onProgress is not null) await onProgress(scanner.Fed);

                var ahead = scanner.Fed - elapsed();
                if (ahead > Lookahead) await Task.Delay(ahead - Lookahead, ct);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
