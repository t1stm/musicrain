using Gaida.Platforms.YouTube;
using Gaida.Platforms.YouTube.Cache;

namespace Pods.Tests;

public class YouTubeCacherTests
{
    [Fact]
    public async Task GetRandomAsyncCapsAtTheRequestedCountAndNeverRepeatsAResult()
    {
        var cacher = new TestCacher(Enumerable.Range(0, 20).Select(i => Result($"yt://{i}")).ToArray());

        var results = await cacher.GetRandomAsync(4);

        Assert.Equal(4, results.Length);
        Assert.Equal(4, results.DistinctBy(result => result.Id).Count());
    }

    /// <summary>A short cache must return what it has, so the endpoint can backfill the difference locally.</summary>
    [Fact]
    public async Task GetRandomAsyncReturnsEverythingWhenTheCacheIsSmallerThanAsked()
    {
        var cacher = new TestCacher(Result("yt://only"));

        Assert.Single(await cacher.GetRandomAsync(10));
        Assert.Empty(await new TestCacher().GetRandomAsync(10));
        Assert.Empty(await cacher.GetRandomAsync(0));
    }

    private static YouTubeResult Result(string id)
    {
        return new YouTubeResult { Id = id, Downloaders = [] };
    }

    private sealed class TestCacher : YouTubeCacher
    {
        public TestCacher(params YouTubeResult[] results) : base(Serilog.Core.Logger.None)
        {
            foreach (var result in results) Cache[result.Id] = result;
        }
    }
}