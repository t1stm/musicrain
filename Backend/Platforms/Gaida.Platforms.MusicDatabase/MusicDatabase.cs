using Gaida.Core.Platforms;
using Gaida.Core.Platforms.Optional.Supports;
using Gaida.Platforms.MusicDatabase.Getters;
using Gaida.Platforms.MusicDatabase.Manager;
using Gaida.Platforms.MusicDatabase.Search_Providers;
using Serilog;

namespace Gaida.Platforms.MusicDatabase;

public sealed class MusicDatabase : Platform, ISupportsSearch, ISupportsRandomResults
{
    private readonly MusicSearchProvider _provider;

    public MusicDatabase(ILogger logger) : base(logger)
    {
        _provider = new MusicSearchProvider(logger);

        SearchProviders = [_provider];
        ContentDownloaders = [new MusicGetter(logger)];
    }

    protected override HashSet<string> SearchIDIdentifiers { get; } = ["audio://"];
    protected override HashSet<string> SearchPlaylistIdentifiers { get; } = [];

    protected override List<SearchProvider> SearchProviders { get; set; }
    protected override List<ContentGetter> ContentDownloaders { get; set; }

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

    /// <summary>The library scan, awaitable. <see cref="Platform.Initialize" /> starts it and returns.</summary>
    public Task InitializeAsync() => _provider.InitializeAsync();

    /// <returns>The library rows an operator is editing — see <see cref="Manager.MusicManager.Find" />.</returns>
    public IReadOnlyList<MusicInfo> FindForAdmin(string? query, int take) => _provider.FindForAdmin(query, take);

    /// <returns>Counts for the admin panel's overview.</returns>
    public object Summary() => _provider.Summary();

    /// <summary>Rewrites one song's names and album, and saves its folder's Info.json.</summary>
    public Task<(MusicInfo? entry, string? error)> EditAsync(string id, IReadOnlyList<string>? titles,
        IReadOnlyList<string>? artists, string? album) => _provider.EditAsync(id, titles, artists, album);

    /// <summary>
    ///     Writes one downloaded track into the library's import folder and indexes it — see
    ///     <see cref="Manager.MusicManager.ImportAsync" />. The pod's /Admin/import-deezer route is the only
    ///     caller: this is how a Deezer track becomes an ordinary audio:// song.
    /// </summary>
    public Task<(MusicInfo? entry, string? error)> ImportAsync(string artist, string title, string? album,
        string extension, Stream content, byte[]? cover = null, CancellationToken cancellationToken = default) =>
        _provider.ImportAsync(artist, title, album, extension, content, cover, cancellationToken);

    /// <summary>
    ///     The library entry behind an ID. <c>/resolve</c> needs the file's path and its lyrics state, and
    ///     neither survives the trip through <see cref="PlatformResult" />.
    /// </summary>
    public MusicInfo? FindEntry(string id) => _provider.FindEntry(id);

    /// <summary>Records what stih found beside one track — see <see cref="Manager.MusicManager.StampLyricsAsync" />.</summary>
    public Task<(MusicInfo? entry, string? error)> StampLyricsAsync(string id, LyricsKind? kind, LyricsOrigin? source)
        => _provider.StampLyricsAsync(id, kind, source);

    /// <returns>Tracks with no lyrics beside them, for stih's backfill sweep.</returns>
    public IReadOnlyList<MusicInfo> MissingLyrics(int take, DateOnly retryBefore) =>
        _provider.MissingLyrics(take, retryBefore);

    /// <returns>The library's answer to a YouTube title, or <c>null</c> when it has none worth offering.</returns>
    public (LocalMatch Match, PlatformResult Result)? FindLocalVariant(string name, string? artist, TimeSpan duration)
    {
        return _provider.FindLocalVariant(name, artist, duration);
    }
}