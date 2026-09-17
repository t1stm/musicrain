using System.Runtime.CompilerServices;
using System.Text.Json;
using Gaida.Bot.Admin;
using Gaida.Bot;
using Gaida.Bot.Commands;
using Gaida.Bot.Enums;
using Gaida.Bot.Gaida;
using Gaida.Bot.Messages;
using Gaida.Bot.Players;
using Microsoft.Extensions.DependencyInjection;

namespace Gaida.Tests;

/// <summary>
/// The bot's three pure parts: the Ogg granule scanner the feed loop paces against, the queue's
/// index arithmetic, and the statusbar's progress bar. Everything else in the bot is Discord.
/// </summary>
public class BotPlaybackTests
{
    private static Track Track(string name, string id = "audio://x", string duration = "00:03:00",
        string artist = "Artist") =>
        new()
        {
            Id = id,
            Name = name,
            Artist = artist,
            ContentUrl = "http://localhost/none",
            Duration = duration
        };

    /// <summary>One Ogg page: "OggS", version, flags, granule, serial, sequence, CRC, segment table.</summary>
    private static byte[] Page(long granule, int payloadLength)
    {
        var segments = new List<byte>();
        var remaining = payloadLength;
        while (remaining >= 255)
        {
            segments.Add(255);
            remaining -= 255;
        }

        segments.Add((byte)remaining);

        var page = new List<byte>();
        page.AddRange("OggS"u8.ToArray());
        page.Add(0);
        page.Add(0);
        page.AddRange(BitConverter.GetBytes(granule));
        page.AddRange(BitConverter.GetBytes(1u));
        page.AddRange(BitConverter.GetBytes(0u));
        page.AddRange(BitConverter.GetBytes(0u));
        page.Add((byte)segments.Count);
        page.AddRange(segments);
        page.AddRange(Enumerable.Repeat((byte)0x42, payloadLength));

        return [.. page];
    }

    [Fact]
    public void ScannerReadsGranulesAsFortyEightKilohertzSamples()
    {
        var scanner = new OggGranuleScanner();

        scanner.Scan(Page(0, 20));
        scanner.Scan(Page(48000, 300));
        scanner.Scan(Page(96000, 10));

        Assert.Equal(96000, scanner.Granule);
        Assert.Equal(TimeSpan.FromSeconds(2), scanner.Fed);
    }

    [Fact]
    public void ScannerSurvivesAPageSplitAcrossReads()
    {
        var whole = new List<byte>();
        whole.AddRange(Page(24000, 40));
        whole.AddRange(Page(48000, 700));

        var bytes = whole.ToArray();
        var scanner = new OggGranuleScanner();

        // Split at every offset that lands inside a header, a segment table and a payload.
        for (var split = 1; split < bytes.Length; split += 7)
        {
            var fresh = new OggGranuleScanner();
            fresh.Scan(bytes.AsSpan(0, split));
            fresh.Scan(bytes.AsSpan(split));

            Assert.Equal(48000, fresh.Granule);
        }

        scanner.Scan(bytes);
        Assert.Equal(48000, scanner.Granule);
    }

    [Fact]
    public void ScannerIgnoresTheMagicWhenItIsOnlyPayload()
    {
        // A page whose payload happens to contain "OggS" must not be read as a page header: the
        // filler after it would parse as an enormous granule and the pacer would stop feeding.
        var page = Page(48000, 64).ToList();
        var payloadStart = page.Count - 64;
        page[payloadStart] = (byte)'O';
        page[payloadStart + 1] = (byte)'g';
        page[payloadStart + 2] = (byte)'g';
        page[payloadStart + 3] = (byte)'S';

        var scanner = new OggGranuleScanner();
        scanner.Scan(page.ToArray());

        Assert.Equal(48000, scanner.Granule);
    }

    [Fact]
    public void QueueWrapsAndRepeatsTheWayTheLoopExpects()
    {
        var queue = new Playlist();
        queue.AddToQueue([Track("one"), Track("two"), Track("three")]);

        Assert.Equal("one - Artist", queue.GetCurrent()!.DisplayName);
        Assert.Equal("two - Artist", queue.GetNext()!.DisplayName);

        queue.Current = 2;
        Assert.Null(queue.GetNext());
        Assert.False(queue.EndOfQueue);

        queue.Current = 3;
        Assert.True(queue.EndOfQueue);
        Assert.Null(queue.GetCurrent());
    }

    [Fact]
    public void RemovingBelowTheCurrentIndexKeepsTheSameTrackPlaying()
    {
        var queue = new Playlist();
        queue.AddToQueue([Track("one"), Track("two"), Track("three")]);
        queue.Current = 2;

        queue.RemoveFromQueue(0);

        // The old bot's Player.RemoveFromQueue decrements Current itself; the queue only removes.
        Assert.Equal("three - Artist", queue.Items[1].DisplayName);
        Assert.Equal(2, queue.Count);
    }

    [Fact]
    public void MoveAndShuffleKeepTheCurrentTrackInPlace()
    {
        var queue = new Playlist();
        queue.AddToQueue([Track("one"), Track("two"), Track("three"), Track("four")]);
        queue.Current = 2;

        Assert.True(queue.Move(0, 3, out var moved));
        Assert.Equal("one - Artist", moved.DisplayName);
        Assert.Equal("one - Artist", queue.Items[3].DisplayName);

        var current = queue.GetCurrent()!;
        queue.ShuffleWithSeed(1234);

        Assert.Equal(0, queue.Current);
        Assert.Equal(current.DisplayName, queue.GetCurrent()!.DisplayName);
        Assert.Equal(1234, queue.RandomSeed);
    }

    [Fact]
    public void ShuffleWithTheSameSeedIsTheSameOrder()
    {
        var first = new Playlist();
        var second = new Playlist();

        foreach (var queue in new[] { first, second })
        {
            queue.AddToQueue([Track("one"), Track("two"), Track("three"), Track("four"), Track("five")]);
            queue.ShuffleWithSeed(99);
        }

        Assert.Equal(first.Items.Select(item => item.Name), second.Items.Select(item => item.Name));
    }

    [Fact]
    public void ClearKeepsWhatIsPlaying()
    {
        var queue = new Playlist();
        queue.AddToQueue([Track("one"), Track("two"), Track("three")]);
        queue.Current = 1;

        queue.Clear();

        Assert.Equal(1, queue.Count);
        Assert.Equal("two - Artist", queue.GetCurrent()!.DisplayName);
        Assert.Equal(0, queue.Current);
    }

    [Fact]
    public void ProgressBarFillsInProportionAndNeverOverruns()
    {
        var statusbar = new Statusbar();

        Assert.Equal(new string('□', 32), statusbar.GenerateProgressbar(0, 1000));
        Assert.Equal(new string('■', 16) + new string('□', 16), statusbar.GenerateProgressbar(500, 1000));
        Assert.Equal(new string('■', 32), statusbar.GenerateProgressbar(1000, 1000));
        Assert.Equal(new string('■', 32), statusbar.GenerateProgressbar(5000, 1000));
    }

    [Fact]
    public void ProgressBarAnimatesWhenTheLengthIsUnknown()
    {
        var statusbar = new Statusbar();

        var first = statusbar.GenerateProgressbar(0, 0);
        var second = statusbar.GenerateProgressbar(0, 0);

        Assert.Equal(32, first.Length);
        Assert.Equal(5, first.Count(character => character == '■'));
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void TrackReadsItsKindFromTheIdAndItsLengthFromTheDuration()
    {
        Assert.Equal("Music", Track("x", "audio://a").Kind);
        Assert.Equal("Youtube Video", Track("x", "yt://a").Kind);
        Assert.Equal("Deezer Track", Track("x", "deezer://a").Kind);
        Assert.Equal(TimeSpan.FromMinutes(3), Track("x").Length);

        // An unparseable duration is the live-stream case, which renders as ∞ rather than throwing.
        Assert.Equal(TimeSpan.Zero, Track("x", "yt://a", "").Length);
    }

    /// <summary>A player with no Discord behind it: enough for the index arithmetic, which is all this checks.</summary>
    private static Player Detached(params string[] names)
    {
        var player = new Player
        {
            Client = null!,
            Api = null!,
            Logger = Serilog.Core.Logger.None,
            Controller = null!
        };

        player.Queue.AddToQueue(names.Select(name => Track(name)));
        return player;
    }

    [Fact]
    public void SkipLeavesTheIndexForThePlayLoopToAdvance()
    {
        var player = Detached("one", "two", "three");

        // The loop increments after every track, so a plain skip moves the index by nothing at all.
        player.Skip();
        Assert.Equal(0, player.Queue.Current);

        player.Skip(3);
        Assert.Equal(2, player.Queue.Current);
    }

    [Fact]
    public void PreviousStopsAtTheStartOfTheQueue()
    {
        var player = Detached("one", "two", "three");
        player.Queue.Current = 2;

        player.Skip(-1);
        Assert.Equal(0, player.Queue.Current);

        // Already at the front: going further back would leave the queue, so nothing moves.
        player.Skip(-1);
        Assert.Equal(0, player.Queue.Current);
    }

    [Fact]
    public void PauseTogglesAndStopsTheClock()
    {
        var player = Detached("one");

        player.Pause();
        Assert.True(player.Paused);

        player.Pause();
        Assert.False(player.Paused);
    }

    [Fact]
    public void ASearchTermPicksTheClosestHit()
    {
        // Search streams hits in the order the pods answer, so the list order means nothing and
        // the first arrival is whichever service was quickest — never a reason to play it.
        var results = new List<Track>
        {
            Track("Sonne (Live aus Berlin)", "yt://live"),
            Track("Sonnenschein", "yt://other"),
            Track("Sonne", "yt://exact")
        };

        Assert.Equal("yt://exact", PlaybackCommands.BestMatch(results, "Sonne")!.Id);
    }

    [Fact]
    public void AnEqualMatchPrefersTheLibraryOverYouTube()
    {
        var results = new List<Track>
        {
            Track("Sonne", "yt://video"),
            Track("Sonne", "deezer://track"),
            Track("Sonne", "audio://library")
        };

        Assert.Equal("audio://library", PlaybackCommands.BestMatch(results, "Sonne")!.Id);
        Assert.Null(PlaybackCommands.BestMatch([], "Sonne"));
    }

    [Fact]
    public void AccentsAndSeparatorsDoNotHandTheHitToACopy()
    {
        // Real search output for "la bomba king africa": the Deezer original carries an accent and gets
        // its " - " from the display name, while a YouTube upload spells the separator out in its title.
        var results = new List<Track>
        {
            Track("La Bomba - King Africa", "yt://copy", artist: "HaciendaCalaDor"),
            Track("La Bomba", "deezer://3297621211", artist: "King \u00c1frica")
        };

        Assert.Equal("deezer://3297621211", PlaybackCommands.BestMatch(results, "la bomba king africa")!.Id);
    }

    [Fact]
    public void TheArtistCountsAsAMatchToo()
    {
        var results = new List<Track>
        {
            Track("Du Hast", "yt://duhast"),
            Track("Sonne", "yt://sonne")
        };

        // The query names the artist, which every result shares, so the title decides.
        Assert.Equal("yt://sonne", PlaybackCommands.BestMatch(results, "Artist Sonne")!.Id);
    }

    [Fact]
    public void TheExampleConfigurationStillBindsToWhatTheBotReads()
    {
        // JSON carries no comments, so the example file is the shape's documentation. This keeps it
        // honest when the configuration type changes under it.
        var path = Path.Combine(SourceDirectory(), "..", "..", "Services", "Gaida.Bot", ".env.example.json");
        var accounts = JsonSerializer.Deserialize<BotParametersConfiguration[]>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(accounts);
        Assert.NotEmpty(accounts);
        Assert.All(accounts, account => Assert.False(string.IsNullOrWhiteSpace(account.Name)));
        Assert.All(accounts, account => Assert.False(string.IsNullOrWhiteSpace(account.Token)));

        // Exactly one master, and prefixes only on it: a secondary that listens for the same prefix
        // answers every command a second time.
        Assert.Single(accounts.Where(account => account.Master));
        Assert.All(accounts.Where(account => !account.Master), account => Assert.Empty(account.Prefixes));
        Assert.NotEmpty(accounts.First(account => account.Master).Prefixes);
    }

    private static string SourceDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

    [Fact]
    public void EveryCommandDependencyResolvesOutOfTheContainer()
    {
        // DSharpPlus builds the command class per invocation with ActivatorUtilities, so a service
        // registered under the wrong type only fails when somebody types a command. This catches it
        // against the registrations Program.cs actually uses, not a copy of them.
        var logger = Serilog.Core.Logger.None;
        var api = new GaidaClient(logger);

        var services = BotServices.Register(new ServiceCollection(), logger, api, new PlayerController(api, logger, new BotEventLog()));
        var provider = services.BuildServiceProvider();

        var commands = ActivatorUtilities.CreateInstance<PlaybackCommands>(provider);

        Assert.NotNull(commands);
    }

    [Fact]
    public void LoopCyclesThroughItsThreeStates()
    {
        var player = Detached("one");

        Assert.Equal(LoopMode.WholeQueue, player.ToggleLoop());
        Assert.Equal(LoopMode.One, player.ToggleLoop());
        Assert.Equal(LoopMode.None, player.ToggleLoop());
    }
}
