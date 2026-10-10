using Gaida.Core.Streams;
using Gaida.Platforms.MusicDatabase;
using Gaida.Platforms.MusicDatabase.Manager;
using Gaida.Platforms.YouTube;
using Gaida.Platforms.YouTube.Getters;
using Serilog.Core;

namespace Pods.Tests;

public class LibraryDatabaseTests : IDisposable
{
    private readonly string _storage = Directory.CreateTempSubdirectory("gaida-librarydb").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_storage, true); } catch (IOException) { /* temp dir */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ACoverIsStoredUnderThePlaceholderAndServedUnderTheDomain()
    {
        var options = new LibraryOptions(Domain: "https://music.example.com/");
        var served = "https://music.example.com/Album_Covers/abc.jpg";

        Assert.Equal("$[DOMAIN]/abc.jpg", options.Stored(served));
        Assert.Equal(served, options.Served("$[DOMAIN]/abc.jpg"));
    }

    [Fact]
    public void WithNoDomainACoverIsStoredAsItIs()
    {
        var options = new LibraryOptions();

        Assert.Equal("/Album_Covers/abc.jpg", options.Stored("/Album_Covers/abc.jpg"));
        Assert.Equal("./", options.StorageDirectory);
        Assert.Equal("./Album_Covers", options.CoverDirectory);
    }

    [Fact]
    public void AnEntryRoundTripsThroughLibraryDb()
    {
        var options = new LibraryOptions(_storage, Domain: "https://music.example.com");
        var database = new LibraryDatabase(options);
        var entry = new MusicInfo
        {
            Id = "ramsonne-x9",
            RelativeLocation = "Rock/Rammstein/Rammstein - Sonne.flac",
            Titles = ["Sonne"],
            Artists = ["Rammstein", "Ramstein"],
            Album = "Mutter",
            Scan = MusicManager.ScanVersion,
            CoverUrl = "https://music.example.com/Album_Covers/abc.jpg",
            LyricsType = LyricsKind.Synchronized,
            LyricsSource = LyricsOrigin.Lrclib,
            Duration = TimeSpan.FromSeconds(272)
        };

        database.Write([entry]);
        var read = Assert.Single(Assert.Single(database.ReadLibrary()).Value);

        Assert.Equal("Rock/Rammstein", LibraryDatabase.FolderOf(read.RelativeLocation!));
        Assert.Equal(entry.Titles, read.Titles);
        Assert.Equal(entry.Artists, read.Artists);
        Assert.Equal("Mutter", read.Album);
        Assert.Equal("$[DOMAIN]/abc.jpg", read.CoverUrl);
        Assert.Equal(LyricsOrigin.Lrclib, read.LyricsSource);
        Assert.Equal(entry.Duration, read.Duration);
    }

    [Fact]
    public async Task ADownloadIsKeptInTheWebmCache()
    {
        var cache = new GetterLocalCache(Logger.None, _storage);
        var result = new YouTubeResult { Id = "yt://abcdefghijk", Downloaders = [cache] };
        using var spreader = new StreamSpreader();

        result.RunCacheProcess(spreader);
        await spreader.WriteAsync(new byte[] { 1, 2, 3 });
        await spreader.CloseAsync();

        Assert.True(File.Exists(Path.Combine(_storage, "abcdefghijk.webm")));
        Assert.Equal(Path.Combine(_storage, "abcdefghijk.webm"), spreader.Path);
    }
}
