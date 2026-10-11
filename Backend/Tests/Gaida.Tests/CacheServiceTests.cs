using System.Net;
using Dunav;
using Microsoft.Extensions.Configuration;
using Serilog.Core;

namespace Gaida.Tests;

/// <summary>Dunav's cache against a canned upstream: coalescing, failures, eviction.</summary>
public class CacheServiceTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("dunav-tests").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch (IOException) { /* temp dir */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task OneFetchServesEveryCallerWithUpstreamsHeaders()
    {
        var upstream = new StubHandler(_ =>
        {
            var response = StubHandler.Reply(HttpStatusCode.OK, "audio bytes", "audio/ogg");
            response.Content.Headers.ContentDisposition = new("inline") { FileName = "a.ogg" };
            return Task.FromResult(response);
        });
        var cache = Cache(upstream);

        var first = await Fetch(cache, "key", out var firstStarted);
        var second = await Fetch(cache, "key", out var secondStarted);

        Assert.True(firstStarted);
        Assert.False(secondStarted);
        Assert.Same(first, second);
        Assert.Equal(1, upstream.Requests);
        Assert.Equal("audio/ogg; charset=utf-8", first.ContentType);
        Assert.Equal("inline; filename=a.ogg", first.ContentDisposition);
        Assert.Equal("audio bytes", await Body(first));
        Assert.True(cache.Has("key"));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, 404)]
    [InlineData(HttpStatusCode.InternalServerError, 502)]
    public async Task AFailedFetchIsAnsweredAndNotKept(HttpStatusCode upstreamStatus, int answered)
    {
        var cache = Cache(new StubHandler(_ => Task.FromResult(StubHandler.Reply(upstreamStatus))));

        var entry = await Fetch(cache, "missing", out _);

        Assert.Equal(answered, entry.Failure);
        Assert.False(cache.Has("missing"));
    }

    [Fact]
    public async Task EvictionUnlinksTheBodyAndForgetsTheKey()
    {
        var cache = Cache(new StubHandler(_ => Task.FromResult(StubHandler.Reply(HttpStatusCode.OK, "x"))));
        var entry = await Fetch(cache, "one", out _);
        await Fetch(cache, "two", out _);
        await Body(entry);

        Assert.True(cache.Evict("one", "test"));
        Assert.False(File.Exists(entry.Body.Path));
        Assert.False(cache.Has("one"));
        Assert.False(cache.Evict("one", "test"));

        Assert.Equal(1, cache.EvictAll());
        Assert.False(cache.Has("two"));
    }

    [Fact]
    public async Task ASweepEvictsWhatHasExpired()
    {
        var cache = Cache(new StubHandler(_ => Task.FromResult(StubHandler.Reply(HttpStatusCode.OK, "x"))),
            retentionMinutes: 0);
        await Body(await Fetch(cache, "stale", out _));

        cache.Sweep();

        Assert.False(cache.Has("stale"));
    }

    [Fact]
    public async Task ASweepOverTheCeilingEvictsTheLeastRecentlyUsedFirst()
    {
        var cache = Cache(new StubHandler(_ => Task.FromResult(StubHandler.Reply(HttpStatusCode.OK, "eleven byte"))),
            maxBytes: 15);
        foreach (var key in (string[])["oldest", "older", "newest"])
        {
            await Body(await Fetch(cache, key, out _));
            await Task.Delay(5);
        }

        cache.Sweep();

        Assert.Equal(["newest"], Keys(cache));
    }

    private static IEnumerable<string> Keys(CacheService cache)
    {
        var snapshot = System.Text.Json.JsonSerializer.SerializeToElement(cache.Snapshot());
        return snapshot.GetProperty("entries").EnumerateArray().Select(entry => entry.GetProperty("Key").GetString()!);
    }

    private CacheService Cache(HttpMessageHandler upstream, int retentionMinutes = 45, long maxBytes = 1000000)
    {
        var options = new DunavOptions
        {
            CacheDir = _directory,
            RetentionMinutes = retentionMinutes,
            MaxBytes = maxBytes
        };

        return new CacheService(new HttpClient(upstream) { BaseAddress = new Uri("http://gaida.test") },
            Logger.None, options);
    }

    private static Task<CacheEntry> Fetch(CacheService cache, string key, out bool started)
    {
        return cache.GetOrStartAsync(key, entry => cache.FetchAsync(entry, "/Audio/x", CancellationToken.None),
            out started);
    }

    private static async Task<string> Body(CacheEntry entry)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // The pump creates the body's file on its first write, which can land just after the fetch returns.
        while (!entry.Body.Closed) await Task.Delay(10, timeout.Token);

        await using var reader = entry.Body.OpenRead();
        using var text = new StreamReader(reader);
        return await text.ReadToEndAsync(timeout.Token);
    }
}
