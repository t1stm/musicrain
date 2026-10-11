using Gaida.Platforms.YouTube;
using Gaida.Platforms.YouTube.Cache;

namespace Pods.Tests;

public class YouTubeCacherTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("youtube-cache-").FullName;

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

        var cacher = new YouTubeCacher(Serilog.Core.Logger.None, legacy);
        cacher.Initialize();

        var imported = await cacher.GetFromCacheAsync("QcqaQ47ywD0");
        Assert.Equal("Отличен 6", imported?.Name);
        Assert.Equal(TimeSpan.FromSeconds(213), imported?.Duration);
        Assert.True(File.Exists(legacy));
        Assert.True(File.Exists(Path.Combine(_directory, "YouTube.db")));

        await File.WriteAllTextAsync(legacy, "[]");
        var reopened = new YouTubeCacher(Serilog.Core.Logger.None, legacy);
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

    [Fact]
    public async Task ForgetTakesTheIdWithOrWithoutItsPrefix()
    {
        var cacher = await Cacher(Result("yt://a"), Result("yt://b"));

        Assert.True(await cacher.ForgetAsync("yt://a"));
        Assert.True(await cacher.ForgetAsync("b"));
        Assert.False(await cacher.ForgetAsync("a"));
        Assert.Equal(0, cacher.Count());
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
        var cacher = new YouTubeCacher(Serilog.Core.Logger.None, Path.Combine(_directory, name + ".db"));
        cacher.Initialize();
        await cacher.AddToCacheAsync(results);
        return cacher;
    }
}
