using System.Net;
using Microsoft.Extensions.Configuration;
using Stih;

namespace Gaida.Tests;

/// <summary>Stih's on-demand path against canned pods and a canned LRCLIB.</summary>
public class StihLyricsTests : IDisposable
{
    private const string Synced = """
                                  {"id":1,"trackName":"Sonne","artistName":"Rammstein","albumName":"Mutter",
                                   "duration":272.0,"instrumental":false,"plainLyrics":"w","syncedLyrics":"[00:01.00]w"}
                                  """;

    private static readonly Serilog.ILogger Quiet = Serilog.Core.Logger.None;

    private readonly string _directory = Directory.CreateTempSubdirectory("stih-tests").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch (IOException) { /* temp dir */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ListenersAskingAtOnceShareOneLookup()
    {
        var release = new TaskCompletionSource();
        var (lyrics, lrcLib) = Build(release.Task);

        var first = lyrics.GetAsync("deezer://1");
        var second = lyrics.GetAsync("deezer://1");
        release.SetResult();

        Assert.Equal("Synchronized", (await first)?.Type);
        Assert.Same(await first, await second);
        Assert.Equal(1, lrcLib.Requests);
    }

    [Fact]
    public async Task OneListenerLeavingDoesNotCancelTheOthers()
    {
        var release = new TaskCompletionSource();
        var (lyrics, _) = Build(release.Task);
        using var leaving = new CancellationTokenSource();

        var first = lyrics.GetAsync("deezer://1", leaving.Token);
        var second = lyrics.GetAsync("deezer://1");
        await leaving.CancelAsync();
        release.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal("Synchronized", (await second)?.Type);
    }

    [Fact]
    public async Task AMissIsCleanAndAFailureIsNot()
    {
        var track = new Track("deezer://1", "Sonne", "Rammstein", "Mutter", TimeSpan.FromSeconds(272), null, null);

        var missing = LrcLib(new StubHandler(request => Task.FromResult(StubHandler.Reply(
            request.RequestUri!.AbsolutePath.Contains("/api/get") ? HttpStatusCode.NotFound : HttpStatusCode.OK,
            "[]"))));
        Assert.Equal((null, true), await missing.FindAsync(track));

        var broken = LrcLib(new StubHandler(_ => throw new HttpRequestException("refused")));
        Assert.Equal((null, false), await broken.FindAsync(track));
    }

    private (Lyrics Lyrics, StubHandler LrcLib) Build(Task release)
    {
        var pods = new StubHandler(_ => Task.FromResult(StubHandler.Reply(HttpStatusCode.OK,
            """{"id":"deezer://1","name":"Sonne","artist":"Rammstein","album":"Mutter","duration":"00:04:32"}""")));
        var lrclib = new StubHandler(async _ =>
        {
            await release;
            return StubHandler.Reply(HttpStatusCode.OK, Synced);
        });

        var configuration = Configuration();
        var data = Path.Combine(_directory, "data");
        var lyrics = new Lyrics(new LyricsIndex(data, Quiet), new LrcLib(new StubFactory(lrclib), configuration, Quiet),
            new Tracks(new StubFactory(pods), configuration, Quiet), new StubFactory(pods), configuration, Quiet);

        return (lyrics, lrclib);
    }

    private LrcLib LrcLib(StubHandler handler)
    {
        return new LrcLib(new StubFactory(handler), Configuration(), Quiet);
    }

    private IConfiguration Configuration()
    {
        return new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LRCLIB_URL"] = "https://lrclib.test",
            ["Deezer:Url"] = "http://deezer.test",
            ["MUSIC_LIBRARY"] = Path.Combine(_directory, "music"),
            ["LYRICS_DATA"] = Path.Combine(_directory, "data")
        }).Build();
    }
}
