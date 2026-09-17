using Gaida.Core;
using Gaida.Core.Streams;
using Gaida.Platforms.YouTube;
using Serilog.Core;
using Xunit.Abstractions;

namespace Pods.Tests;

/// <summary>
///     The one test that goes out to YouTube for real: an ID resolves, downloads, and every reader of the
///     resulting <see cref="StreamSpreader" /> gets identical bytes. The spreader's own behaviour is covered
///     without a network in Gaida.Tests — this is here for the pod's half of it.
/// </summary>
public class YouTubeDownloadTests(ITestOutputHelper output)
{
    [Fact]
    public async Task TestDownloading()
    {
        const int streamCount = 16;
        output.WriteLine("Starting download test.");
        var audioManager = new AudioManager(Logger.None);

        audioManager.RegisterPlatform(new YouTube(Logger.None));

        var result = await audioManager.SearchId("yt://dQw4w9WgXcQ");
        Assert.True(result is not null, "YouTube search for 'dQw4w9WgXcQ' failed.");

        output.WriteLine("Found YouTube result.");

        var streamSpreader = await result.GetContentDataAsync();
        Assert.True(streamSpreader is not null, "YouTube download failed.");

        output.WriteLine("Downloading result.");

        var bodies = await Task.WhenAll(Enumerable.Range(0, streamCount).Select(async _ =>
        {
            await using var reader = streamSpreader.OpenRead();
            using var sink = new MemoryStream();
            await reader.CopyToAsync(sink);
            return sink.ToArray();
        }));

        var first = bodies[0];
        Assert.NotEmpty(first);

        var index = 0;
        foreach (var body in bodies)
        {
            Assert.Equal(first, body);
            output.WriteLine($"Equality check for [{index++}] is successful.");
        }
    }
}
