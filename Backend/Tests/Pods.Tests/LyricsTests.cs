using System.Text.Json;
using Gaida.Platforms.MusicDatabase;
using Gaida.Platforms.MusicDatabase.Manager;

namespace Pods.Tests;

/// <summary>
///     The library half of the lyrics feature — see LYRICS_1_LIBRARY_PLAN.md. Nothing here fetches
///     anything: reconciliation is two File.Exists against a temp directory, and the other two are a
///     LINQ pass over an in-memory list.
/// </summary>
public class LyricsTests : IDisposable
{
    private readonly string _storage = Directory.CreateTempSubdirectory("gaida-lyrics").FullName;

    public LyricsTests()
    {
        // MusicManager reads STORAGE as a process variable, the way the pods set it — see
        // Gaida.Pods.MusicDatabase/Program.cs:30. Tests in one class run sequentially, so the
        // per-test value never races another one in here.
        Environment.SetEnvironmentVariable("STORAGE", _storage, EnvironmentVariableTarget.Process);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("STORAGE", null, EnvironmentVariableTarget.Process);
        try { Directory.Delete(_storage, true); } catch (IOException) { /* temp dir */ }
    }

    // ── ReconcileLyrics ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ASidecarWithNoEntryStateIsRecordedWithUnknownProvenance()
    {
        var entry = Track("Rock/Rammstein/Rammstein - Sonne.flac", ".lrc");

        Assert.True(MusicManager.ReconcileLyrics(entry));
        Assert.Equal(LyricsKind.Synchronized, entry.LyricsType);
        // Unknown provenance means it was already there, which is the state nothing overwrites.
        Assert.Null(entry.LyricsSource);
    }

    [Fact]
    public void APlainFileIsUnsynchronizedAndATimedOneWinsOverIt()
    {
        var entry = Track("Rock/Rammstein/Rammstein - Sonne.flac", ".txt");
        Assert.True(MusicManager.ReconcileLyrics(entry));
        Assert.Equal(LyricsKind.Unsynchronized, entry.LyricsType);

        File.WriteAllText(Path.Combine(_storage, "Rock/Rammstein/Rammstein - Sonne.lrc"), "[00:12.34]Alle warten");
        Assert.True(MusicManager.ReconcileLyrics(entry));
        Assert.Equal(LyricsKind.Synchronized, entry.LyricsType);
    }

    [Fact]
    public void AFileThatIsGoneClearsTheProvenanceButNotTheRetryWindow()
    {
        var entry = Track("Rock/Rammstein/Rammstein - Sonne.flac");
        entry.LyricsType = LyricsKind.Synchronized;
        entry.LyricsSource = LyricsOrigin.Lrclib;
        entry.LyricsChecked = new DateOnly(2026, 1, 1);

        Assert.True(MusicManager.ReconcileLyrics(entry));
        Assert.Null(entry.LyricsType);
        Assert.Null(entry.LyricsSource);
        // Someone deleting a bad .lrc should not trigger an immediate re-fetch of the same bad .lrc.
        Assert.Equal(new DateOnly(2026, 1, 1), entry.LyricsChecked);
    }

    [Fact]
    public void AnUnchangedEntryDoesNotDirtyItsFolder()
    {
        var entry = Track("Rock/Rammstein/Rammstein - Sonne.flac", ".lrc");
        entry.LyricsType = LyricsKind.Synchronized;
        entry.LyricsSource = LyricsOrigin.Lrclib;

        Assert.False(MusicManager.ReconcileLyrics(entry));
        Assert.Equal(LyricsOrigin.Lrclib, entry.LyricsSource);
    }

    // ── Info.json ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheFieldsRoundTripThroughInfoJsonByName()
    {
        var entry = new MusicInfo
        {
            Id = "ramsonne-x9",
            LyricsType = LyricsKind.Synchronized,
            LyricsSource = LyricsOrigin.Lrclib,
            LyricsChecked = new DateOnly(2026, 9, 15)
        };

        var json = JsonSerializer.Serialize(entry, MusicInfo.SerializerOptions);
        Assert.Contains("\"LyricsType\": \"Synchronized\"", json);
        Assert.Contains("\"LyricsSource\": \"LRCLIB\"", json);
        Assert.Contains("\"LyricsChecked\": \"2026-09-15\"", json);

        var read = JsonSerializer.Deserialize<MusicInfo>(json, MusicInfo.SerializerOptions)!;
        Assert.Equal(LyricsKind.Synchronized, read.LyricsType);
        Assert.Equal(LyricsOrigin.Lrclib, read.LyricsSource);
    }

    [Fact]
    public void AnInventedValueFailsAtLoadRatherThanReachingAListener()
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<MusicInfo>("""{"LyricsType": "synced"}""", MusicInfo.SerializerOptions));
    }

    // ── MissingLyrics ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NeverCheckedTracksComeBeforeRetriedOnes()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var manager = Seeded(
            Entry("retried", checkedOn: today.AddDays(-100)),
            Entry("fresh"),
            Entry("older", checkedOn: today.AddDays(-200)));

        var missing = manager.MissingLyrics(10, today.AddDays(-30));
        Assert.Equal(["fresh", "older", "retried"], missing.Select(song => song.Id));
    }

    [Fact]
    public void TheRetryWindowIsHonoured()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var manager = Seeded(Entry("yesterday", checkedOn: today.AddDays(-1)));

        Assert.Empty(manager.MissingLyrics(10, today.AddDays(-30)));
        Assert.Single(manager.MissingLyrics(10, today));
    }

    [Fact]
    public void ATrackWithLyricsIsNeverOffered()
    {
        var manager = Seeded(Entry("has-them", kind: LyricsKind.Unsynchronized), Entry("wants-them"));

        Assert.Equal(["wants-them"], manager.MissingLyrics(10, DateOnly.FromDateTime(DateTime.UtcNow))
            .Select(song => song.Id));
    }

    // ── StampLyricsAsync ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnUnknownIdIsRefused()
    {
        var (entry, error) = await Seeded().StampLyricsAsync("nobody", LyricsKind.Synchronized, LyricsOrigin.Lrclib);

        Assert.Null(entry);
        Assert.Equal("No song with that ID.", error);
    }

    [Fact]
    public async Task FoundNothingRecordsTheDayAndLeavesTheTypeNull()
    {
        var manager = Seeded(Entry("wants-them"));

        var (entry, error) = await manager.StampLyricsAsync("wants-them", null, null);

        Assert.Null(error);
        Assert.Null(entry!.LyricsType);
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow), entry.LyricsChecked);
    }

    [Fact]
    public async Task AStampOnATrackThatAlreadyHasLyricsIsRejected()
    {
        var manager = Seeded(Entry("has-them", kind: LyricsKind.Unsynchronized));

        var (entry, error) = await manager.StampLyricsAsync("has-them", LyricsKind.Synchronized, LyricsOrigin.Lrclib);

        Assert.Null(entry);
        Assert.Equal("That entry already has lyrics.", error);
    }

    [Fact]
    public async Task AHitIsWrittenIntoTheFoldersInfoJson()
    {
        var manager = Seeded(Entry("wants-them"));

        var (_, error) = await manager.StampLyricsAsync("wants-them", LyricsKind.Synchronized, LyricsOrigin.Deezer);
        Assert.Null(error);

        var written = await File.ReadAllTextAsync(Path.Combine(_storage, "Rock/Rammstein/Info.json"));
        Assert.Contains("\"LyricsSource\": \"Deezer\"", written);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>An entry whose audio file exists, optionally with a lyrics sidecar beside it.</summary>
    private MusicInfo Track(string relative, string? sidecar = null)
    {
        var path = Path.Combine(_storage, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0]);

        if (sidecar is not null) File.WriteAllText(Path.ChangeExtension(path, sidecar), "Alle warten");

        return new MusicInfo { Id = "ramsonne-x9", RelativeLocation = relative };
    }

    private static MusicInfo Entry(string id, LyricsKind? kind = null, DateOnly? checkedOn = null) => new()
    {
        Id = id,
        RelativeLocation = $"Rock/Rammstein/{id}.flac",
        LyricsType = kind,
        LyricsChecked = checkedOn
    };

    private TestMusicManager Seeded(params MusicInfo[] songs)
    {
        Directory.CreateDirectory(Path.Combine(_storage, "Rock/Rammstein"));
        return new TestMusicManager(songs);
    }

    /// <summary>The library in memory, without a scan — the same shim MusicManagerTests uses.</summary>
    private sealed class TestMusicManager : MusicManager
    {
        public TestMusicManager(params MusicInfo[] songs) : base(Serilog.Core.Logger.None)
        {
            Songs = [.. songs];
        }
    }
}
