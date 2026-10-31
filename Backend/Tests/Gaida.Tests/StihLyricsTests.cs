using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
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

    [Fact]
    public async Task ARegistrationNamesAKnownSourceOrIsRefused()
    {
        var (lyrics, _) = Build(Task.CompletedTask);

        Assert.NotNull(await lyrics.RegisterAsync(new RegisterDto("deezer://1", "Invented", null, "[00:01.00]w")));
        Assert.NotNull(await lyrics.RegisterAsync(new RegisterDto("deezer://1", "7", null, "[00:01.00]w")));
        Assert.Null(await lyrics.RegisterAsync(new RegisterDto("deezer://1", "Generated", null, "[00:01.00]w")));

        var served = await lyrics.GetAsync("deezer://1");
        Assert.Equal(("Synchronized", "Generated"), (served?.Type, served?.Source));
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

        var options = Options();
        var data = Path.Combine(_directory, "data");
        var lyrics = new Lyrics(new LyricsIndex(data, Quiet), new LrcLib(new StubFactory(lrclib), options, Quiet),
            new Tracks(new StubFactory(pods), options, Quiet), new StubFactory(pods), options, Quiet);

        return (lyrics, lrclib);
    }

    private LrcLib LrcLib(StubHandler handler)
    {
        return new LrcLib(new StubFactory(handler), Options(), Quiet);
    }

    private IOptions<StihOptions> Options()
    {
        return Microsoft.Extensions.Options.Options.Create(new StihOptions
        {
            LrcLibUrl = "https://lrclib.test",
            DeezerUrl = "http://deezer.test",
            MusicLibrary = Path.Combine(_directory, "music"),
            LyricsData = Path.Combine(_directory, "data")
        });
    }
}
