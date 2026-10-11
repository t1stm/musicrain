using Dom;
using Dunav;
using Gaida.API;
using Microsoft.Extensions.Configuration;
using Moliv;
using Oko;
using Selo;
using Stih;

namespace Gaida.Tests;

/// <summary>Every service's options read the keys the deployments already set, and keep the old defaults.</summary>
public class OptionsBindingTests
{
    [Fact]
    public void GaidaApiReadsItsKeys()
    {
        var options = Bind<GaidaApiOptions>(new()
        {
            ["PublicApiBaseUrl"] = "https://api.test",
            ["THUMBNAIL_CACHE"] = "/thumbs",
            ["Random:Shares:youtube"] = "0.25",
            ["Resolve:Concurrency"] = "7"
        });

        Assert.Equal(("https://api.test", "/thumbs", 0.25, 7),
            (options.PublicApiBaseUrl, options.ThumbnailCache, options.RandomYouTubeShare, options.ResolveConcurrency));
        Assert.Equal((0.4, 4), (Bind<GaidaApiOptions>(new()).RandomYouTubeShare, new GaidaApiOptions().ResolveConcurrency));
    }

    [Fact]
    public void DunavReadsItsKeys()
    {
        var options = Bind<DunavOptions>(new()
        {
            ["Gaida:Url"] = "http://gaida.test",
            ["Dunav:RetentionMinutes"] = "5",
            ["Dunav:MaxBytes"] = "1024",
            ["Dunav:CacheDir"] = "/cache"
        });

        Assert.Equal(("http://gaida.test", 5, 1024L, "/cache"),
            (options.GaidaUrl, options.RetentionMinutes, options.MaxBytes, options.CacheDir));

        var defaults = Bind<DunavOptions>(new());
        Assert.Equal(("http://localhost:8080", 45, 20L * 1024 * 1024 * 1024, "/tmp/dunav"),
            (defaults.GaidaUrl, defaults.RetentionMinutes, defaults.MaxBytes, defaults.CacheDir));
    }

    [Fact]
    public void DomReadsItsKeys()
    {
        var options = Bind<DomOptions>(new()
        {
            ["Dom:DataFile"] = "/data/dom.db",
            ["Dom:CoverDir"] = "/data/covers",
            ["Moliv:Url"] = "http://moliv.test",
            ["ADMIN_TOKEN"] = "secret"
        });

        Assert.Equal(("/data/dom.db", "/data/covers", "http://moliv.test", "secret"),
            (options.DataFile, options.CoverDir, options.MolivUrl, options.AdminToken));
        Assert.Equal(("dom.db", "covers"), (new DomOptions().DataFile, new DomOptions().CoverDir));
    }

    [Fact]
    public void MolivReadsItsKeys()
    {
        var options = Bind<MolivOptions>(new()
        {
            ["Moliv:DataFile"] = "/data/plays.db",
            ["Dom:Url"] = "http://dom.test",
            ["ADMIN_TOKEN"] = "secret"
        });

        Assert.Equal(("/data/plays.db", "http://dom.test", "secret"),
            (options.DataFile, options.DomUrl, options.AdminToken));
        Assert.Equal(("plays.db", "http://dom:8080"), (new MolivOptions().DataFile, new MolivOptions().DomUrl));
    }

    [Fact]
    public void OkoReadsItsKeysAndTargets()
    {
        var options = Bind<OkoOptions>(new()
        {
            ["ADMIN_USERNAME"] = "kris",
            ["ADMIN_PASSWORD"] = "hunter2",
            ["ADMIN_TOKEN"] = "secret",
            ["Targets:0:Name"] = "dunav",
            ["Targets:0:Url"] = "http://dunav.test"
        });

        Assert.Equal(("kris", "hunter2", "secret"), (options.Username, options.Password, options.AdminToken));
        Assert.Equal("http://dunav.test", Assert.Single(options.Targets).Url);
        Assert.Empty(Bind<OkoOptions>(new()).Targets);
    }

    [Fact]
    public void SeloReadsItsKey()
    {
        Assert.Equal("http://gaida.test", Bind<SeloOptions>(new() { ["Gaida:Url"] = "http://gaida.test" }).GaidaUrl);
    }

    [Fact]
    public void StihReadsItsKeysAndStillShrugsOffNumbersThatDoNotParse()
    {
        var options = Bind<StihOptions>(new()
        {
            ["LRCLIB_URL"] = "https://lrclib.test",
            ["LRCLIB_USER_AGENT"] = "agent",
            ["MUSIC_LIBRARY"] = "/m",
            ["LYRICS_DATA"] = "/l",
            ["Local:Url"] = "http://local.test",
            ["Deezer:Url"] = "http://deezer.test",
            ["ADMIN_TOKEN"] = "secret",
            ["LYRICS_SWEEP"] = "false",
            ["LYRICS_SWEEP_DELAY_MS"] = "250",
            ["LYRICS_SWEEP_IDLE_HOURS"] = "not a number",
            ["LYRICS_RETRY_DAYS"] = "1.5"
        });

        Assert.Equal(("https://lrclib.test", "agent", "/m", "/l", "http://local.test", "http://deezer.test", "secret"),
            (options.LrcLibUrl, options.LrcLibUserAgent, options.MusicLibrary, options.LyricsData, options.LocalUrl,
                options.DeezerUrl, options.AdminToken));
        Assert.False(options.SweepEnabled);
        Assert.Equal(TimeSpan.FromMilliseconds(250), options.SweepDelay);
        Assert.Equal(TimeSpan.FromHours(6), options.SweepIdle);
        Assert.Equal(30, options.SweepRetryDays);
        Assert.Equal(TimeSpan.FromDays(1.5), options.RetryAfter);

        var defaults = Bind<StihOptions>(new());
        Assert.True(defaults.SweepEnabled);
        Assert.Equal(("", "/music", "/lyrics"), (defaults.LrcLibUrl, defaults.MusicLibrary, defaults.LyricsData));
    }

    private static T Bind<T>(Dictionary<string, string?> values) where T : new()
    {
        var options = new T();
        new ConfigurationBuilder().AddInMemoryCollection(values).Build().Bind(options);
        return options;
    }
}
