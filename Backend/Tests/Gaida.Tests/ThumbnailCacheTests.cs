using System.Net;
using Gaida.API;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Gaida.Tests;

public class ThumbnailCacheTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("gaida-thumbs").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch (IOException) { /* temp dir */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ACoverServedOnceIsFoundTheSecondTime()
    {
        var upstream = new StubHandler(_ =>
            Task.FromResult(StubHandler.Reply(HttpStatusCode.OK, "png bytes", "image/png")));
        var cache = new ThumbnailCache(Options.Create(new GaidaApiOptions { ThumbnailCache = _directory }),
            new StubFactory(upstream));

        Assert.Null(cache.Find("audio://a"));

        using var response = await cache.FetchAsync("http://covers.test/a.png", CancellationToken.None);
        using var served = new MemoryStream();
        await cache.KeepWhileServingAsync("audio://a", await response.Content.ReadAsStreamAsync(), "image/png", served,
            CancellationToken.None);

        var kept = cache.Find("audio://a");
        Assert.NotNull(kept);
        Assert.EndsWith(".png", kept);
        Assert.Equal("image/png", ThumbnailCache.ContentTypeFor(kept));
        Assert.Equal(served.ToArray(), await File.ReadAllBytesAsync(kept));
    }
}
