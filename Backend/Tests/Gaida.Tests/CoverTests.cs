using System.Security.Cryptography;
using Gaida.API;

namespace Gaida.Tests;

public class CoverTests
{
    [Fact]
    public async Task PumpWritesTheResponseAndPublishesTheCache()
    {
        var (directory, temporary, cache) = Paths();
        var payload = RandomNumberGenerator.GetBytes(70_000);
        var response = new MemoryStream();

        try
        {
            await ThumbnailCache.PumpAsync(new MemoryStream(payload), response, temporary, cache);

            Assert.Equal(payload, response.ToArray());
            Assert.Equal(payload, await File.ReadAllBytesAsync(cache));
            Assert.False(File.Exists(temporary));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task PumpLeavesNoCacheEntryWhenTheDownloadBreaks()
    {
        var (directory, temporary, cache) = Paths();

        try
        {
            await Assert.ThrowsAsync<IOException>(() =>
                ThumbnailCache.PumpAsync(new BreakingStream(), new MemoryStream(), temporary, cache));

            Assert.False(File.Exists(cache));
            Assert.False(File.Exists(temporary));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static (string Directory, string Temporary, string Cache) Paths()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"gaida-cover-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return (directory, Path.Combine(directory, "entry.part"), Path.Combine(directory, "entry.jpg"));
    }

    /// <summary>An upstream body that dies partway through, the way a dropped connection does.</summary>
    private sealed class BreakingStream : MemoryStream
    {
        private bool _served;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_served) throw new IOException("The response ended prematurely.");
            _served = true;
            buffer.Span[..16].Clear();
            return ValueTask.FromResult(16);
        }
    }
}