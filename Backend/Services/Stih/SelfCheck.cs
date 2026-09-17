using System.Net;

namespace Stih;

/// <summary>
///     The halves of stih worth proving runnable with no host and no network: the parser, the matcher,
///     and the index's round trip. Run with <c>dotnet run --project Stih -- --self-check</c>.
/// </summary>
/// <remarks>
///     Same reasoning as Dunav's and Dom's: the deployed image carries no test project, and a service
///     that cannot parse an LRC file is a service that cannot serve. The path fence is in here too —
///     it is the one rule whose failure writes into somebody's music library.
/// </remarks>
internal static class SelfCheck
{
    public static bool Run()
    {
        var ok = Parser() & Matcher() & Index() & Paths() & Client();
        Console.WriteLine(ok ? "selftest OK" : "selftest FAILED");
        return ok;
    }

    private static bool Parser()
    {
        var plain = LrcParser.Parse("One more time\nWe're gonna celebrate");
        var timed = LrcParser.Parse("[ar:Rammstein]\n[00:12.34]Alle warten\n[00:04.20]auf das Licht\n");
        var refrain = LrcParser.Parse("[00:12.34][01:45.00]same refrain");
        var gap = LrcParser.Parse("[00:10.00]words\n[00:20.00]\n[00:30.00]more");
        var offset = LrcParser.Parse("[offset:-500]\n[00:10.00]words");
        var hours = LrcParser.Parse("[1:02:03.50]late");
        var mixed = LrcParser.Parse("[00:10.00]good\nnot timed at all\n[0x:yy]rubbish");

        var ok = Check("plain text has no timestamps",
                     plain.Kind == LyricsKind.Unsynchronized && plain.Lines.Count == 2 &&
                     plain.Lines.All(line => line.At is null))
                 // Out of order in the file, in order in the answer -- and the [ar:] header is not a line.
                 & Check("timed lines are sorted and the header dropped",
                     timed.Kind == LyricsKind.Synchronized && timed.Lines.Count == 2 &&
                     timed.Lines[0].Text == "auf das Licht" && Near(timed.Lines[0].At, 4.2))
                 & Check("one refrain at two timestamps is two lines",
                     refrain.Lines.Count == 2 && Near(refrain.Lines[1].At, 105))
                 & Check("an instrumental gap keeps its empty line",
                     gap.Lines.Count == 3 && gap.Lines[1].Text.Length == 0)
                 & Check("an offset moves every timestamp", Near(offset.Lines[0].At, 9.5))
                 & Check("[h:mm:ss.xx] is an hour in", Near(hours.Lines[0].At, 3723.5))
                 // Some timestamps means the untimed lines are the header, not words anybody sings.
                 & Check("untimed lines are dropped once anything is timed",
                     mixed.Kind == LyricsKind.Synchronized && mixed.Lines.Count == 1)
                 & Check("the plain block is the lines joined",
                     LrcParser.TextOf(timed.Lines) == "auf das Licht\nAlle warten");

        return ok;
    }

    private static bool Matcher()
    {
        var track = new Track("audio://x", "Sonne", "Rammstein", "Mutter", TimeSpan.FromSeconds(272), null, null);

        var right = Candidate(1, "Sonne", "Rammstein", 273, synced: "[00:12.34]Alle warten");
        var tooLong = Candidate(2, "Sonne", "Rammstein", 274);
        var live = Candidate(3, "Sonne (Live)", "Rammstein", 272);
        var plainOne = Candidate(4, "Sonne", "Rammstein", 272, synced: null);
        var wrongSong = Candidate(5, "Bestrafe mich", "Rammstein", 272);
        var instrumental = Candidate(6, "Sonne", "Rammstein", 272, instrumental: true);

        return Check("a one-second delta is the same recording", Matching.Best(track, [right])?.Id == 1)
               & Check("two seconds is not", Matching.Best(track, [tooLong]) is null)
               & Check("a live version is not the plain track", Matching.Best(track, [live]) is null)
               & Check("synchronized beats plain at the same length",
                   Matching.Best(track, [plainOne, right])?.Id == 1)
               & Check("another song by the same band is not this one",
                   Matching.Best(track, [wrongSong]) is null)
               & Check("an instrumental has no words", Matching.Best(track, [instrumental]) is null)
               & Check("nothing above the bar is nothing", Matching.Best(track, []) is null);
    }

    private static bool Index()
    {
        using var scratch = new ScratchDirectory();
        var row = new LyricsRow("audio://x", LyricsKind.Synchronized, LyricsOrigin.Lrclib,
            LyricsVolume.Library, "Rock/x.lrc", DateTimeOffset.UtcNow);

        var index = new LyricsIndex(scratch.Path, Quiet);
        index.Record(row);
        index.DisposeAsync().AsTask().GetAwaiter().GetResult();

        var reloaded = new LyricsIndex(scratch.Path, Quiet);
        var round = reloaded.Get("audio://x");
        // Read before the torn-file check below overwrites it.
        var written = File.ReadAllText(Path.Combine(scratch.Path, "Lyrics.json"));

        var old = new LyricsRow("audio://old", null, null, null, null, DateTimeOffset.UtcNow.AddDays(-40));
        var recent = new LyricsRow("audio://recent", null, null, null, null, DateTimeOffset.UtcNow.AddDays(-1));
        var hit = new LyricsRow("audio://hit", LyricsKind.Unsynchronized, null, LyricsVolume.Own, "deezer/1.txt",
            DateTimeOffset.UtcNow.AddYears(-3));

        File.WriteAllText(Path.Combine(scratch.Path, "Lyrics.json"), "{not json");
        var torn = new LyricsIndex(scratch.Path, Quiet);

        return Check("a row round-trips through the file",
                   round is { Type: LyricsKind.Synchronized, Source: LyricsOrigin.Lrclib, Path: "Rock/x.lrc" })
               & Check("the file names the values rather than numbering them",
                   written.Contains("\"Synchronized\""))
               & Check("a miss inside the retry window is believed",
                   LyricsIndex.IsFresh(recent, TimeSpan.FromDays(30)))
               & Check("a miss outside it is not", !LyricsIndex.IsFresh(old, TimeSpan.FromDays(30)))
               & Check("a hit is never re-fetched", LyricsIndex.IsFresh(hit, TimeSpan.FromDays(30)))
               & Check("a torn index loads as empty rather than failing the boot", torn.Count == 0);
    }

    private static bool Paths()
    {
        using var scratch = new ScratchDirectory();
        var library = Path.Combine(scratch.Path, "music");
        var data = Path.Combine(scratch.Path, "lyrics");
        Directory.CreateDirectory(library);

        var lyrics = Build(library, data);

        var song = new Track("audio://x", "Sonne", "Rammstein", "Mutter", TimeSpan.FromSeconds(272),
            "Rock/Rammstein/Rammstein - Sonne.flac", null);
        var escaping = song with { RelativeLocation = "../../etc/passwd.flac" };
        var deezer = new Track("deezer://3135556", "One More Time", "Daft Punk", null, TimeSpan.Zero, null, null);
        var hostile = deezer with { Id = "deezer://../../etc/passwd" };

        var beside = lyrics.Destination(song, LyricsKind.Synchronized);
        var plain = lyrics.Destination(song, LyricsKind.Unsynchronized);
        var own = lyrics.Destination(deezer, LyricsKind.Synchronized);

        return Check("a library track's words land beside its audio",
                   beside.Relative == "Rock/Rammstein/Rammstein - Sonne.lrc" &&
                   beside.Absolute == Path.Combine(library, "Rock/Rammstein/Rammstein - Sonne.lrc"))
               & Check("unsynchronized words get the other extension", plain.Relative!.EndsWith(".txt"))
               & Check("a Deezer track lands in stih's own volume",
                   own.Volume == LyricsVolume.Own && own.Relative == Path.Combine("deezer", "3135556.lrc"))
               & Check("a path climbing out of the library is refused",
                   lyrics.Destination(escaping, LyricsKind.Synchronized).Absolute is null)
               & Check("an ID with a separator in it is refused",
                   lyrics.Destination(hostile, LyricsKind.Synchronized).Absolute is null);
    }

    /// <summary>
    ///     The LRCLIB client against a stubbed transport: no network, but every rule that matters is a
    ///     rule about what comes back over one.
    /// </summary>
    private static bool Client()
    {
        const string body = """
                            {"id":1,"trackName":"Sonne","artistName":"Rammstein","albumName":"Mutter",
                             "duration":272.0,"instrumental":false,"plainLyrics":"w","syncedLyrics":"[00:00.00]w"}
                            """;

        var track = new Track("audio://x", "Sonne", "Rammstein", "Mutter", TimeSpan.FromSeconds(272), null, null);

        var direct = new Stub((_, _) => Reply(HttpStatusCode.OK, body));
        var exact = Find(direct, track);

        // A 404 on /api/get means "not at that length", not "no such song", so the search is the second half.
        var falling = new Stub((path, _) => path.Contains("/api/get")
            ? Reply(HttpStatusCode.NotFound, "{}")
            : Reply(HttpStatusCode.OK, "[" + body + "]"));
        var fuzzy = Find(falling, track);

        var limited = new Stub((_, _) =>
        {
            var response = Reply(HttpStatusCode.TooManyRequests, "{}");
            response.Headers.TryAddWithoutValidation("Retry-After", "30");
            return response;
        });
        var lrcLib = Client(limited);
        var refused = lrcLib.FindAsync(track).GetAwaiter().GetResult();

        var broken = new Stub((_, _) => throw new HttpRequestException("connection refused"));
        var unreachable = Client(broken);
        var failed = unreachable.FindAsync(track).GetAwaiter().GetResult();

        var silent = Client(new Stub((_, _) => Reply(HttpStatusCode.OK, "[]")), url: string.Empty);

        return Check("an exact hit is used as it is", exact?.Id == 1)
               & Check("a 404 falls through to the search", fuzzy?.Id == 1 && falling.Requested.Count == 2)
               & Check("every request identifies the client",
                   direct.Agents.Count > 0 && direct.Agents.All(agent => agent is { Length: > 0 }))
               & Check("a 429 pauses the sweep and writes no row",
                   refused is null && !lrcLib.LastWasClean &&
                   lrcLib.PausedUntil > DateTimeOffset.UtcNow.AddSeconds(20))
               & Check("an unreachable LRCLIB is not a miss",
                   failed is null && !unreachable.LastWasClean)
               & Check("an empty LRCLIB_URL asks nothing at all",
                   !silent.Enabled && silent.FindAsync(track).GetAwaiter().GetResult() is null);
    }

    // ── plumbing ────────────────────────────────────────────────────────────────────────────────

    private static LrcLibResult? Find(Stub stub, Track track)
    {
        return Client(stub).FindAsync(track).GetAwaiter().GetResult();
    }

    private static LrcLib Client(Stub stub, string url = "https://lrclib.test")
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LRCLIB_URL"] = url,
            ["LRCLIB_USER_AGENT"] = "musicrain-selfcheck"
        }).Build();

        return new LrcLib(new StubFactory(stub), configuration, Quiet);
    }

    private static HttpResponseMessage Reply(HttpStatusCode status, string json)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
    }

    /// <summary>One canned transport, recording what was asked and with which User-Agent.</summary>
    private sealed class Stub(Func<string, HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];
        public List<string?> Agents { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.PathAndQuery;
            Requested.Add(path);
            Agents.Add(request.Headers.UserAgent.ToString());

            return Task.FromResult(answer(path, request));
        }
    }

    private sealed class StubFactory(Stub stub) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(stub, false);
    }


    private static readonly Serilog.ILogger Quiet = new Serilog.LoggerConfiguration().CreateLogger();

    /// <summary>A Lyrics with no pods configured: every path here is the part that touches no network.</summary>
    private static Lyrics Build(string library, string data)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MUSIC_LIBRARY"] = library,
            ["LYRICS_DATA"] = data
        }).Build();

        var factory = new ServiceCollection().AddHttpClient().BuildServiceProvider()
            .GetRequiredService<IHttpClientFactory>();

        var index = new LyricsIndex(data, Quiet);
        return new Lyrics(index, new LrcLib(factory, configuration, Quiet),
            new Tracks(factory, configuration, Quiet), factory, configuration, Quiet);
    }

    private static LrcLibResult Candidate(int id, string title, string artist, double duration,
        string? synced = "[00:00.00]words", bool instrumental = false)
    {
        return new LrcLibResult(id, title, artist, "Mutter", duration, instrumental, "words", synced);
    }

    private static bool Near(double? value, double expected)
    {
        return value is not null && Math.Abs(value.Value - expected) < 0.01;
    }

    private static bool Check(string what, bool ok)
    {
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}");
        return ok;
    }

    private sealed class ScratchDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("stih-selfcheck").FullName;

        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch (IOException) { /* temp dir */ }
        }
    }
}
