using Gaida.Platforms.MusicDatabase;
using Gaida.Platforms.MusicDatabase.Manager;

namespace Pods.Tests;

public class LocalVariantTests
{
    [Fact]
    public void OffersTheLibraryCopyOfAnOfficialUpload()
    {
        var manager = new TestMusicManager(Song("sonne", "Sonne", "Rammstein"));

        var match = manager.FindLocalVariant("Rammstein - Sonne (Official Video)", "RammsteinOfficial",
            TimeSpan.FromMinutes(4));

        Assert.NotNull(match);
        Assert.Equal(LocalMatchKind.Same, match.Kind);
    }

    [Fact]
    public void ReachesACyrillicTitleThroughItsRomanizedSpelling()
    {
        // The case the absolute threshold in SearchByTerm rejects: Romanize writes "Mitnicharyu", the file
        // says "Mitnichariu", and the Cyrillic segment is what lands the exact hit instead.
        var song = new MusicInfo
        {
            Id = "mitnichariu",
            Titles = ["Митничарю", "Mitnichariu"],
            Artists = ["Лия", "Lia"]
        };
        var manager = new TestMusicManager(song);

        var match = manager.FindLocalVariant("Lia - Mitnichariu/ Лия - Митничарю", "Payner", TimeSpan.Zero);

        Assert.NotNull(match);
        Assert.Equal(song, match.Song);
    }

    [Fact]
    public void OffersThePlainCopyOfATaggedUpload()
    {
        var manager = new TestMusicManager(Song("sonne", "Sonne", "Rammstein"));

        var match = manager.FindLocalVariant("Rammstein - Sonne (Live at Wacken)", null, TimeSpan.FromMinutes(4));

        Assert.NotNull(match);
        Assert.Equal(LocalMatchKind.Variant, match.Kind);
        Assert.Equal(["live"], match.YouTubeTags);
        Assert.Empty(match.LibraryTags);
    }

    [Fact]
    public void NeverAnswersAPlainUploadWithATaggedLibraryTrack()
    {
        var manager = new TestMusicManager(Song("sonne", "Sonne (Instrumental)", "Rammstein"));

        Assert.Null(manager.FindLocalVariant("Rammstein - Sonne", null, TimeSpan.FromMinutes(4)));
    }

    [Fact]
    public void RejectsAnArtistWhoseTitleIsWrong()
    {
        // The guard against a library full of one artist matching everything they ever released.
        var manager = new TestMusicManager(Song("mutter", "Mutter", "Rammstein"));

        Assert.Null(manager.FindLocalVariant("Rammstein - Sonne", null, TimeSpan.FromMinutes(4)));
    }

    [Fact]
    public void ReportsTheDurationDeltaWithoutRejectingOnIt()
    {
        var song = Song("sonne", "Sonne", "Rammstein");
        song.Duration = TimeSpan.FromSeconds(272);
        var manager = new TestMusicManager(song);

        var match = manager.FindLocalVariant("Rammstein - Sonne", null, TimeSpan.FromSeconds(284));

        Assert.NotNull(match);
        Assert.Equal(LocalMatchKind.Same, match.Kind);
        Assert.Equal(-12, match.DurationDelta.TotalSeconds);
    }

    private static MusicInfo Song(string id, string title, string artist)
    {
        return new MusicInfo
        {
            Id = id,
            Titles = [title],
            Artists = [artist]
        };
    }

    private sealed class TestMusicManager : MusicManager
    {
        public TestMusicManager(params MusicInfo[] songs) : base(Serilog.Core.Logger.None)
        {
            Songs = [.. songs];
        }
    }
}