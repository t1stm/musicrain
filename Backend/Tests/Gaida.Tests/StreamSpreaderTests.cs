using System.Security.Cryptography;
using Xunit.Abstractions;

namespace Gaida.Tests;

public class StreamSpreaderTests(ITestOutputHelper output)
{
    /// <summary>Every reader sees the same bytes in the same order, whatever else is reading alongside it.</summary>
    [Fact]
    public async Task CorrectDataOrder()
    {
        await using var streamSpreader = new StreamSpreader();
        var randomBytes = RandomNumberGenerator.GetBytes(1048576);

        var readers = Enumerable.Range(0, 16).Select(async _ =>
        {
            await using var reader = streamSpreader.OpenRead();
            using var sink = new MemoryStream();
            await reader.CopyToAsync(sink);
            return sink.ToArray();
        }).ToArray();

        output.WriteLine("Set up destinations.");

        await new MemoryStream(randomBytes).CopyToAsync(streamSpreader, 1 << 12);
        await streamSpreader.CloseAsync();

        output.WriteLine("Copied and closed stream.");

        var index = 0;
        foreach (var array in await Task.WhenAll(readers))
        {
            Assert.Equal(randomBytes.Length, array.Length);
            Assert.Equal(randomBytes, array);
            output.WriteLine($"Stream check [{index++}] is successful.");
        }
    }

    /// <summary>
    ///     Expiry disposes spreaders while range requests may still be reading them. A reader holds its own
    ///     handle, so the unlink is invisible to it: it finishes the body rather than faulting part-way.
    /// </summary>
    [Fact]
    public async Task DisposeDoesNotFaultConcurrentReads()
    {
        var streamSpreader = new StreamSpreader();
        var randomBytes = RandomNumberGenerator.GetBytes(1 << 20);

        await new MemoryStream(randomBytes).CopyToAsync(streamSpreader, 1 << 12);
        await streamSpreader.CloseAsync();

        // Opened before the dispose, exactly as an in-flight response would be.
        var readers = Enumerable.Range(0, 8).Select(_ => streamSpreader.OpenRead()).ToArray();

        await streamSpreader.DisposeAsync();

        foreach (var reader in readers)
        {
            using var sink = new MemoryStream();
            await reader.CopyToAsync(sink);
            await reader.DisposeAsync();

            Assert.Equal(randomBytes, sink.ToArray());
        }
    }

    /// <summary>
    ///     A file that already exists is adopted, not copied: no write happens, the body is complete from the
    ///     outset, and disposing the spreader must leave the original untouched.
    /// </summary>
    [Fact]
    public async Task ExistingFileIsAdoptedNotCopied()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var randomBytes = RandomNumberGenerator.GetBytes(1 << 18);
        await File.WriteAllBytesAsync(path, randomBytes);

        try
        {
            var streamSpreader = StreamSpreader.FromExistingFile(path);

            Assert.True(streamSpreader.Closed, "An adopted file is complete from the outset.");
            Assert.Equal(randomBytes.Length, streamSpreader.Length);
            Assert.Equal(path, streamSpreader.Path);

            await using (var reader = streamSpreader.OpenRead())
            {
                using var sink = new MemoryStream();
                await reader.CopyToAsync(sink);
                Assert.Equal(randomBytes, sink.ToArray());
            }

            await streamSpreader.DisposeAsync();
            Assert.True(File.Exists(path), "Disposing an adopted spreader must not delete the source file.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A scratch body cleans up after itself; <see cref="StreamSpreader.KeepAs" /> keeps it instead.</summary>
    [Fact]
    public async Task DeleteOnCloseAndKeepAs()
    {
        var scratch = new StreamSpreader();
        var scratchPath = scratch.Path;
        await scratch.WriteAsync(RandomNumberGenerator.GetBytes(4096));
        await scratch.CloseAsync();
        await scratch.DisposeAsync();
        Assert.False(File.Exists(scratchPath), "A scratch body must delete itself on dispose.");

        var destination = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var randomBytes = RandomNumberGenerator.GetBytes(4096);

        var kept = new StreamSpreader();
        var temporaryPath = kept.Path;
        kept.KeepAs(destination);
        await kept.WriteAsync(randomBytes);
        await kept.CloseAsync();

        try
        {
            Assert.Equal(destination, kept.Path);
            Assert.Equal(randomBytes, await File.ReadAllBytesAsync(destination));
            Assert.False(File.Exists(temporaryPath), "The body was moved, not copied.");

            await kept.DisposeAsync();
            Assert.True(File.Exists(destination), "A kept body must survive dispose.");
        }
        finally
        {
            File.Delete(destination);
        }
    }

    /// <summary>A reader that arrives after the body is finished still gets all of it.</summary>
    [Fact]
    public async Task ClosedCopyTest()
    {
        await using var streamSpreader = new StreamSpreader();
        var randomBytes = RandomNumberGenerator.GetBytes(4096);

        await streamSpreader.WriteAsync(randomBytes);
        await streamSpreader.CloseAsync();
        await Task.Delay(200);

        await using var reader = streamSpreader.OpenRead();
        using var sink = new MemoryStream();
        await reader.CopyToAsync(sink);

        Assert.Equal(randomBytes, sink.ToArray());
    }

    /// <summary>
    ///     A reader whose client vanished must not affect anyone else. Under the old push model one throwing
    ///     subscriber could truncate the stream for every other; readers now own their handles, so a failure
    ///     is contained to the one that failed.
    /// </summary>
    [Fact]
    public async Task FaultingReaderDoesNotStarveOthers()
    {
        await using var streamSpreader = new StreamSpreader();
        var randomBytes = RandomNumberGenerator.GetBytes(1 << 16);

        using var poisonedSource = new CancellationTokenSource();

        // ReSharper disable AccessToDisposedClosure -- both readers are awaited below, before the `using`
        // declarations at the top of the test dispose the spreader and the token source.
        var poisoned = Task.Run(async () =>
        {
            await using var reader = streamSpreader.OpenRead();
            await reader.CopyToAsync(new ThrowingStream(), poisonedSource.Token);
        }, poisonedSource.Token);

        var healthy = Task.Run(async () =>
        {
            await using var reader = streamSpreader.OpenRead();
            using var sink = new MemoryStream();
            await reader.CopyToAsync(sink, poisonedSource.Token);
            return sink.ToArray();
        }, poisonedSource.Token);

        await new MemoryStream(randomBytes).CopyToAsync(streamSpreader, 1 << 12, poisonedSource.Token);
        await streamSpreader.CloseAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => poisoned);
        Assert.Equal(randomBytes, await healthy);
        // ReSharper restore AccessToDisposedClosure
    }

    /// <summary>Stands in for a response whose client has gone: every write throws.</summary>
    private sealed class ThrowingStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            throw new ObjectDisposedException("IFeatureCollection");
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new ObjectDisposedException("IFeatureCollection");
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
