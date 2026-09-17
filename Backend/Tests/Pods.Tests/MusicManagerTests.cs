using Gaida.Platforms.MusicDatabase;
using Gaida.Platforms.MusicDatabase.Manager;

namespace Pods.Tests;

public class MusicManagerTests
{
    [Fact]
    public void SearchByTermReturnsEverySongByMatchingArtist()
    {
        var firstSong = Song("first", "Around the World", "Daft Punk");
        var secondSong = Song("second", "Something About Us", "Daft Punk");
        var unrelatedSong = Song("unrelated", "One More Time", "The Marvelettes");
        var manager = new TestMusicManager(firstSong, secondSong, unrelatedSong);

        var results = manager.SearchByTerm("Daft Punk").ToList();

        Assert.Equal([firstSong, secondSong], results);
    }

    [Fact]
    public void SearchByTermMatchesRomanizedArtistName()
    {
        var song = new MusicInfo
        {
            Id = "romanized",
            Titles = ["Притури се планината", "Prituri se planinata"],
            Artists = ["Стефка Съботинова", "Stefka Sabotinova"]
        };
        var manager = new TestMusicManager(song);

        var results = manager.SearchByTerm("Stefka Sabotinova").ToList();

        Assert.Equal([song], results);
    }

    [Theory]
    [InlineData("Sayuki")]
    [InlineData("Maki")]
    [InlineData("Maki & Sayuki")]
    public void SearchByTermFindsEitherHalfOfACompoundArtist(string term)
    {
        var song = Song("wings", "Wings of Fire", "Maki & Sayuki");
        var manager = new TestMusicManager(song, Song("other", "One More Time", "Daft Punk"));

        Assert.Equal([song], manager.SearchByTerm(term).ToList());
    }

    [Fact]
    public void SearchByTermReachesEveryVariantOfTheName()
    {
        // The tag said "Mako", the folder said "Maki". Both are in the array, both are searchable, and
        // nothing had to decide which one was the typo.
        var song = new MusicInfo
            { Id = "wings", Titles = ["Wings of Fire"], Artists = ["Mako & Sayuki", "Maki & Sayuki"] };
        var manager = new TestMusicManager(song);

        Assert.Equal([song], manager.SearchByTerm("Maki").ToList());
        Assert.Equal([song], manager.SearchByTerm("Mako").ToList());
    }

    [Fact]
    public void BrowseReturnsOnlyTheImmediateChildrenOfAFolder()
    {
        var loose = At("Bulgarian/loose.wv");
        var manager = new TestMusicManager(
            At("Bulgarian/Estrada/Argirovi/a.wv"),
            At("Bulgarian/Estrada/Argirovi/b.wv"),
            At("Bulgarian/Narodna/c.wv"),
            At("Bulgarians/not-this-one.wv"),
            loose);

        var (folders, files) = manager.Browse("Bulgarian");

        // The count is everything beneath the folder, not just the files directly in it.
        Assert.Equal([("Estrada", 2), ("Narodna", 1)], folders);
        // "Bulgarians/" must not answer a browse of "Bulgarian".
        Assert.Equal([loose], files);
        Assert.Empty(manager.Browse("Bulgarian/Nope").Folders);
        Assert.Empty(manager.Browse("Bulgarian/Nope").Files);
    }

    [Fact]
    public void BrowseTreatsAnEmptyPathAsTheRoot()
    {
        var manager = new TestMusicManager(At("Bulgarian/Estrada/a.wv"), At("Japanese/b.wv"));

        Assert.Equal([("Bulgarian", 1), ("Japanese", 1)], manager.Browse(null).Folders);
        Assert.Equal(manager.Browse(null).Folders, manager.Browse("/").Folders);
    }

    [Fact]
    public void AlbumTracksComeBackInThePlaylistsOrder()
    {
        // Folder order is not album order, and the folder holds more than the album does.
        var songs = new[] { At("Rock/Queen/b.wv"), At("Rock/Queen/a.wv"), At("Rock/Queen/loose.wv") };
        string[] lines =
        [
            "#EXTM3U",
            "#EXTINF:223,Queen - A",
            "a.wv",
            "b.wv",
            "gone.wv", // deleted since the playlist was written
            "a.wvc",   // a WavPack correction file, never indexed
            ""
        ];

        Assert.Equal(["Rock/Queen/a.wv", "Rock/Queen/b.wv"],
            MusicManager.Ordered(songs, lines).Select(song => song.RelativeLocation));
    }

    private static MusicInfo At(string location)
    {
        var song = Song(location, location.Split('/')[^1], "Anyone");
        song.RelativeLocation = location;
        return song;
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