using Gaida.Platforms.YouTube;
using Gaida.Platforms.YouTube.Cache;
using Gaida.Platforms.YouTube.SearchProviders;

namespace Pods.Tests;

public class YouTubeCacherTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("youtube-cache-").FullName;

    private string Webm => Path.Combine(_directory, "webm");

    public void Dispose()
    {
        Directory.Delete(_directory, true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task GetRandomAsyncCapsAtTheRequestedCountAndNeverRepeatsAResult()
    {
        var cacher = await Cacher([.. Enumerable.Range(0, 20).Select(i => Result($"yt://{i}"))]);

        var results = await cacher.GetRandomAsync(4);

        Assert.Equal(4, results.Length);
        Assert.Equal(4, results.DistinctBy(result => result.Id).Count());
    }

    /// <summary>A short cache must return what it has, so the endpoint can backfill the difference locally.</summary>
    [Fact]
    public async Task GetRandomAsyncReturnsEverythingWhenTheCacheIsSmallerThanAsked()
    {
        var cacher = await Cacher(Result("yt://only"));

        Assert.Single(await cacher.GetRandomAsync(10));
        Assert.Empty(await (await Cacher("empty")).GetRandomAsync(10));
        Assert.Empty(await cacher.GetRandomAsync(0));
    }

    [Fact]
    public async Task AResultRoundTripsByItsBareIdAndKeepsWhatItWasFirstCachedAs()
    {
        var first = Result("yt://dQw4w9WgXcQ");
        first.Name = "Never Gonna Give You Up";
        first.Duration = TimeSpan.FromSeconds(213);
        var cacher = await Cacher(first);

        var again = Result("yt://dQw4w9WgXcQ");
        again.Name = "Renamed";
        await cacher.AddToCacheAsync([again]);

        var cached = await cacher.GetFromCacheAsync("dQw4w9WgXcQ");
        Assert.Equal("yt://dQw4w9WgXcQ", cached?.Id);
        Assert.Equal("Never Gonna Give You Up", cached?.Name);
        Assert.Equal(TimeSpan.FromSeconds(213), cached?.Duration);
        Assert.Null(await cacher.GetFromCacheAsync("missing"));
    }

    /// <summary>
    ///     A volume an older version filled: YOUTUBE_CACHE_DB still names the JSON, and the database lands
    ///     beside it with every entry in it. The JSON stays for that older version.
    /// </summary>
    [Fact]
    public async Task TheOldJsonIsImportedOnceAndLeftWhereItWas()
    {
        var legacy = Path.Combine(_directory, "YouTube.json");
        await File.WriteAllTextAsync(legacy, """
            [{"Name":"Отличен 6","Artist":"DIAPASON RECORDS","Album":null,"Duration":"00:03:33",
              "ThumbnailUrl":"https://img.youtube.com/vi/QcqaQ47ywD0/hqdefault.jpg","OriginalTitle":null,
              "OriginalArtist":null,"Id":"yt://QcqaQ47ywD0"}]
            """);

        var cacher = new YouTubeCacher(Serilog.Core.Logger.None, legacy, Webm);
        cacher.Initialize();

        var imported = await cacher.GetFromCacheAsync("QcqaQ47ywD0");
        Assert.Equal("Отличен 6", imported?.Name);
        Assert.Equal(TimeSpan.FromSeconds(213), imported?.Duration);
        Assert.True(File.Exists(legacy));
        Assert.True(File.Exists(Path.Combine(_directory, "YouTube.db")));

        await File.WriteAllTextAsync(legacy, "[]");
        var reopened = new YouTubeCacher(Serilog.Core.Logger.None, legacy, Webm);
        reopened.Initialize();
        Assert.NotNull(await reopened.GetFromCacheAsync("QcqaQ47ywD0"));
    }

    /// <summary>Oko pages with the pin page 1 handed back, so a result cached meanwhile shifts nothing.</summary>
    [Fact]
    public async Task PagesStayPinnedToTheRowsPageOneSaw()
    {
        var cacher = await Cacher([.. Enumerable.Range(0, 5).Select(i => Result($"yt://{i}"))]);

        var first = await cacher.PageAsync(null, 0, 2, null);
        await cacher.AddToCacheAsync([Result("yt://late")]);
        var second = await cacher.PageAsync(null, 2, 2, first.Top);

        Assert.Equal(["yt://4", "yt://3"], first.Rows.Select(row => row.Id));
        Assert.Equal(["yt://2", "yt://1"], second.Rows.Select(row => row.Id));
        Assert.Equal(5, second.Total);
        Assert.Equal(6, (await cacher.PageAsync(null, 0, 2, null)).Total);
    }

    /// <summary>SQLite folds ASCII only, and most of the real cache is Bulgarian.</summary>
    [Fact]
    public async Task SearchIgnoresCaseInCyrillicAndMatchesTheId()
    {
        var song = Result("yt://QcqaQ47ywD0");
        song.Name = "Тази песен не е за Любов";
        var cacher = await Cacher(song, Result("yt://other"));

        Assert.Equal(1, (await cacher.PageAsync("любов", 0, 10, null)).Total);
        Assert.Equal(1, (await cacher.PageAsync("QcqaQ47", 0, 10, null)).Total);
        Assert.Equal(0, (await cacher.PageAsync("qcqaq47", 0, 10, null)).Total);
    }

    /// <summary>
    ///     The webm cache on disk is the truth: a start fills the index from it, and a first page takes in what
    ///     landed or left since. Only .webm counts, because that is all the getter serves.
    /// </summary>
    [Fact]
    public async Task DownloadsAreIndexedFromDiskAndListedBesideTheirResults()
    {
        Directory.CreateDirectory(Webm);
        await File.WriteAllBytesAsync(Path.Combine(Webm, "a.webm"), new byte[10]);
        await File.WriteAllBytesAsync(Path.Combine(Webm, "orphan.webm"), new byte[30]);
        await File.WriteAllBytesAsync(Path.Combine(Webm, "old.mp4"), new byte[5]);
        await File.WriteAllBytesAsync(Path.Combine(Webm, "b.webm.old"), new byte[5]);
        // Older first, so the index's order is the order they were downloaded in.
        File.SetLastWriteTimeUtc(Path.Combine(Webm, "a.webm"), new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var cacher = await Cacher(Result("yt://a"), Result("yt://b"));

        Assert.Equal((2, 2, 40L), cacher.Count());

        // Every result, newest first, then the downloads no result describes.
        var all = await cacher.PageAsync(null, 0, 10, null);
        Assert.Equal(["yt://b", "yt://a", "yt://orphan"], all.Rows.Select(row => row.Id));
        Assert.Equal(3, all.Total);
        Assert.Null(all.Rows[0].Format);
        Assert.Equal(("webm", 10L), (all.Rows[1].Format, all.Rows[1].Bytes));
        Assert.Null(all.Rows[2].Name);

        // A page that starts past the results reads on into the downloads.
        Assert.Equal(["yt://orphan"], (await cacher.PageAsync(null, 2, 10, all.Top)).Rows.Select(row => row.Id));
        Assert.Equal(["yt://orphan", "yt://a"],
            (await cacher.PageAsync(null, 0, 10, null, downloaded: true)).Rows.Select(row => row.Id));

        File.Delete(Path.Combine(Webm, "a.webm"));
        Assert.Equal(1, (await cacher.PageAsync(null, 0, 10, null, downloaded: true)).Total);
    }

    [Fact]
    public async Task EvictDeletesTheWebmAndNeverLeavesTheCache()
    {
        Directory.CreateDirectory(Webm);
        await File.WriteAllBytesAsync(Path.Combine(Webm, "a.webm"), new byte[10]);
        await File.WriteAllBytesAsync(Path.Combine(_directory, "outside.webm"), new byte[10]);
        var cacher = await Cacher(Result("yt://a"));

        Assert.True(await cacher.IsDownloadedAsync("a"));
        Assert.False(await cacher.EvictAsync("../outside"));
        Assert.True(File.Exists(Path.Combine(_directory, "outside.webm")));

        Assert.True(await cacher.EvictAsync("yt://a"));
        Assert.False(File.Exists(Path.Combine(Webm, "a.webm")));
        Assert.False(await cacher.IsDownloadedAsync("yt://a"));
        Assert.NotNull(await cacher.GetFromCacheAsync("a"));
    }

    /// <summary>
    ///     YouTube.GetByIdAsync remembers what it looked up unless it came out of the cache. This is the flag
    ///     it decides on, so a cache hit is never written back.
    /// </summary>
    [Fact]
    public async Task AResultReadOutOfTheCacheSaysSo()
    {
        var cacher = await Cacher(Result("yt://a"));

        var hit = await new YouTubeSearchProviderCached(Serilog.Core.Logger.None, cacher).GetByIdAsync("a");

        Assert.True(Assert.IsType<YouTubeResult>(hit).Cached);
        Assert.False(Result("yt://b").Cached);
    }

    [Fact]
    public async Task ForgetTakesTheIdWithOrWithoutItsPrefix()
    {
        var cacher = await Cacher(Result("yt://a"), Result("yt://b"));

        Assert.True(await cacher.ForgetAsync("yt://a"));
        Assert.True(await cacher.ForgetAsync("b"));
        Assert.False(await cacher.ForgetAsync("a"));
        Assert.Equal(0, cacher.Count().Results);
    }

    private static YouTubeResult Result(string id)
    {
        return new YouTubeResult { Id = id, Downloaders = [] };
    }

    private Task<YouTubeCacher> Cacher(params YouTubeResult[] results)
    {
        return Cacher(Guid.NewGuid().ToString("n"), results);
    }

    private async Task<YouTubeCacher> Cacher(string name, params YouTubeResult[] results)
    {
        var cacher = new YouTubeCacher(Serilog.Core.Logger.None, Path.Combine(_directory, name + ".db"), Webm);
        cacher.Initialize();
        await cacher.AddToCacheAsync(results);
        return cacher;
    }
}
