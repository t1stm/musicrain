using Gaida.Core.Platforms;
using Gaida.Core.Platforms.Optional.Supports;
using Gaida.Core.Utils;
using Gaida.Platforms.MusicDatabase.Manager;
using Serilog;

namespace Gaida.Platforms.MusicDatabase.SearchProviders;

public class MusicSearchProvider(ILogger logger, LibraryOptions options) : SearchProvider(logger),
    ISupportsId, ISupportsSearch, ISupportsRandomResults
{
    private readonly MusicManager _musicManager = new(logger, options);
    public override int Priority => 99;

    public Task<PlatformResult?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        var found = _musicManager.SearchById(id);
        return Task.FromResult<PlatformResult?>(found?.ToMusicResult(ContentDownloaders, options.StorageDirectory));
    }

    public IAsyncEnumerable<PlatformResult> GetRandomResults(int count,
        CancellationToken cancellationToken = default)
    {
        return ToResults(_musicManager.GetRandomSongs(count));
    }

    public IAsyncEnumerable<PlatformResult> SearchKeywords(string keywords,
        CancellationToken cancellationToken = default)
    {
        return ToResults(_musicManager.SearchByTerm(keywords));
    }

    public IAsyncEnumerable<PlatformResult> GetArtistSongs(string artist)
    {
        return ToResults(_musicManager.GetArtistSongs(artist));
    }

    public IAsyncEnumerable<PlatformResult> GetAlbumSongs(string artist, string album)
    {
        return ToResults(_musicManager.GetAlbumSongs(artist, album));
    }

    /// <returns>One level of the library's folder tree: subfolders with their song counts, and the songs in the folder.</returns>
    public (IReadOnlyList<(string Name, int Songs)> Folders, IReadOnlyList<PlatformResult> Files) Browse(string? path)
    {
        var (folders, files) = _musicManager.Browse(path);
        return (folders, [.. files.Select(PlatformResult (song) => song.ToMusicResult(ContentDownloaders, options.StorageDirectory))]);
    }

    public IAsyncEnumerable<PlatformResult> BrowseTracks(string? path)
    {
        return ToResults(_musicManager.BrowseTracks(path));
    }

    internal MusicManager Library => _musicManager;

    public (LocalMatch Match, PlatformResult Result)? FindLocalVariant(string name, string? artist, TimeSpan duration)
    {
        var match = _musicManager.FindLocalVariant(name, artist, duration);
        return match is null ? null : (match, match.Song.ToMusicResult(ContentDownloaders, options.StorageDirectory));
    }

    protected override void Initialize()
    {
        Logger.Debug("Initializing MusicSearchProvider");
        _ = ScanAtBootAsync();
        base.Initialize();
    }

    /// <summary>Nothing awaits the boot scan, so a failure would otherwise leave an empty library and no trace.</summary>
    private async Task ScanAtBootAsync()
    {
        try
        {
            await _musicManager.Initialize();
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "The library scan at boot failed; the library is empty until a rescan works");
        }
    }

    private IAsyncEnumerable<PlatformResult> ToResults(IEnumerable<MusicInfo> songs)
    {
        return songs.Select(PlatformResult (song) => song.ToMusicResult(ContentDownloaders, options.StorageDirectory)).AsAsync();
    }
}
