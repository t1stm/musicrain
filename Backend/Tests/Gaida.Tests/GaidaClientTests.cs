using System.Net;
using Gaida.Bot.Gaida;
using Serilog.Core;

namespace Gaida.Tests;

/// <summary>The bot's five calls against a real loopback server: what each makes of a good and a bad answer.</summary>
public class GaidaClientTests
{
    private const string Track = """
                                 {"id":"audio://a","name":"Sonne","artist":"Rammstein","contentUrl":"http://x/a",
                                  "duration":"00:04:32"}
                                 """;

    [Fact]
    public async Task ResolveReadsTheResolutionAndRefusalIsNull()
    {
        using var server = new LoopbackServer(context => context.Request.QueryString["query"] == "known"
            ? LoopbackServer.Write(context, HttpStatusCode.OK, $$"""{"kind":"local","query":"known","result":{{Track}}}""")
            : LoopbackServer.Write(context, HttpStatusCode.NotFound));
        var client = new GaidaClient(Logger.None, server.BaseUrl);

        var known = await client.ResolveAsync("known");
        Assert.Equal("local", known?.Kind);
        Assert.Equal("audio://a", known?.Result?.Id);

        Assert.Null(await client.ResolveAsync("unknown"));
    }

    [Fact]
    public async Task ResolveAgainstNothingIsNull()
    {
        var client = new GaidaClient(Logger.None, "http://127.0.0.1:1");

        Assert.Null(await client.ResolveAsync("anything"));
    }

    [Fact]
    public async Task ACancelledResolveIsACancellationNotAMiss()
    {
        using var server = new LoopbackServer(_ => Task.CompletedTask);
        var client = new GaidaClient(Logger.None, server.BaseUrl);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ResolveAsync("x", cancelled.Token));
    }

    [Fact]
    public async Task SearchStreamsTracksAndAFailureIsAnEmptyList()
    {
        using var server = new LoopbackServer(context => context.Request.QueryString["query"] == "good"
            ? LoopbackServer.Write(context, HttpStatusCode.OK, $"[{Track},{Track}]")
            : LoopbackServer.Write(context, HttpStatusCode.InternalServerError, "boom"));
        var client = new GaidaClient(Logger.None, server.BaseUrl);

        Assert.Equal(2, await client.SearchAsync("good").CountAsync());
        Assert.Equal(0, await client.SearchAsync("bad").CountAsync());
    }

    [Fact]
    public async Task LyricsAreNullWhenThereAreNone()
    {
        using var server = new LoopbackServer(context => context.Request.QueryString["id"] == "audio://words"
            ? LoopbackServer.Write(context, HttpStatusCode.OK, """{"type":"Unsynchronized","text":"la la"}""")
            : LoopbackServer.Write(context, HttpStatusCode.NoContent));
        var client = new GaidaClient(Logger.None, server.BaseUrl);

        Assert.Equal("la la", (await client.LyricsAsync("audio://words"))?.Text);
        Assert.Null(await client.LyricsAsync("audio://none"));
    }

    [Fact]
    public async Task AudioIsTheOpenResponseOrNull()
    {
        using var server = new LoopbackServer(context => context.Request.QueryString["id"] == "audio://a"
            ? LoopbackServer.Write(context, HttpStatusCode.OK, "OggS", "audio/ogg")
            : LoopbackServer.Write(context, HttpStatusCode.NotFound));
        var client = new GaidaClient(Logger.None, server.BaseUrl);

        using var audio = await client.OpenAudioAsync("audio://a", 96);
        Assert.Equal("OggS", await audio!.Content.ReadAsStringAsync());

        Assert.Null(await client.OpenAudioAsync("audio://missing", 96));
    }
}
