using Gaida.Core.Platforms;
using Gaida.Core.Platforms.Optional.Supports;
using Gaida.Platforms.MusicDatabase.Getters;
using Gaida.Platforms.MusicDatabase.Manager;
using Gaida.Platforms.MusicDatabase.SearchProviders;
using Serilog;

namespace Gaida.Platforms.MusicDatabase;

public sealed class MusicDatabase : Platform, ISupportsSearch, ISupportsRandomResults
{
    private readonly MusicSearchProvider _provider;

    public MusicDatabase(ILogger logger, LibraryOptions options) : base(logger)
    {
        _provider = new MusicSearchProvider(logger, options);

        SearchProviders = [_provider];
        ContentDownloaders = [new MusicGetter(logger)];
    }

    protected override HashSet<string> SearchIdIdentifiers { get; } = [MusicResult.Prefix];

    public IAsyncEnumerable<PlatformResult> GetRandomResults(int count,
        CancellationToken cancellationToken = default)
    {
        return _provider.GetRandomResults(count, cancellationToken);
    }

    public IAsyncEnumerable<PlatformResult> SearchKeywords(string keywords,
        CancellationToken cancellationToken = default)
    {
        return _provider.SearchKeywords(keywords, cancellationToken);
    }

    public IAsyncEnumerable<PlatformResult> GetArtistSongs(string artist)
    {
        return _provider.GetArtistSongs(artist);
    }

    /// <returns>One album's tracks in playlist order — see <see cref="Manager.MusicManager.GetAlbumSongs" />.</returns>
    public IAsyncEnumerable<PlatformResult> GetAlbumSongs(string artist, string album)
    {
        return _provider.GetAlbumSongs(artist, album);
    }

    /// <returns>One level of the library's folder tree, for the explorer.</returns>
    public (IReadOnlyList<(string Name, int Songs)> Folders, IReadOnlyList<PlatformResult> Files) Browse(string? path)
    {
        return _provider.Browse(path);
    }

    /// <returns>Every track beneath a folder — see <see cref="Manager.MusicManager.BrowseTracks" />.</returns>
    public IAsyncEnumerable<PlatformResult> BrowseTracks(string? path)
    {
        return _provider.BrowseTracks(path);
    }

    /// <summary>
    ///     The library itself, for what the pod does with it besides playing it: the admin editor, imports, and
    ///     stih's lyrics bookkeeping.
    /// </summary>
    public MusicManager Library => _provider.Library;

    /// <returns>The library's answer to a YouTube title, or <c>null</c> when it has none worth offering.</returns>
    public (LocalMatch Match, PlatformResult Result)? FindLocalVariant(string name, string? artist, TimeSpan duration)
    {
        return _provider.FindLocalVariant(name, artist, duration);
    }
}