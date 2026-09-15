using Gaida.Core.Platforms;
using Gaida.Core.Platforms.Optional.Supports;
using Gaida.Core.Utils;
using Gaida.Platforms.MusicDatabase.Manager;
using Serilog;

namespace Gaida.Platforms.MusicDatabase.Search_Providers;

public class MusicSearchProvider(ILogger logger) : SearchProvider(logger),
    ISupportsID, ISupportsSearch, ISupportsRandomResults
{
    protected readonly MusicManager MusicManager = new(logger);
    public override string PlatformIdentifier => "audio://";
    public override int Priority => 99;

    public Task<PlatformResult?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        var found = MusicManager.SearchById(id);
        return Task.FromResult<PlatformResult?>(found?.ToMusicResult(ContentDownloaders));
    }

    public IAsyncEnumerable<PlatformResult> GetRandomResults(int count,
        CancellationToken cancellationToken = default)
    {
        return ToResults(MusicManager.GetRandomSongs(count));
    }

    public IAsyncEnumerable<PlatformResult> SearchKeywords(string keywords,
        CancellationToken cancellationToken = default)
    {
        return ToResults(MusicManager.SearchByTerm(keywords));
    }

    public IAsyncEnumerable<PlatformResult> GetArtistSongs(string artist)
    {
        return ToResults(MusicManager.GetArtistSongs(artist));
    }

    public IAsyncEnumerable<PlatformResult> GetAlbumSongs(string artist, string album)
    {
        return ToResults(MusicManager.GetAlbumSongs(artist, album));
    }

    /// <returns>One level of the library's folder tree: subfolders with their song counts, and the songs in the folder.</returns>
    public (IReadOnlyList<(string Name, int Songs)> Folders, IReadOnlyList<PlatformResult> Files) Browse(string? path)
    {
        var (folders, files) = MusicManager.Browse(path);
        return (folders, [.. files.Select(PlatformResult (song) => song.ToMusicResult(ContentDownloaders))]);
    }

    /// <summary>The library scan, awaitable — <see cref="Initialize" /> starts it and does not wait.</summary>
    public Task InitializeAsync() => MusicManager.Initialize();

    /// <summary>Admin: the library as rows to edit.</summary>
    public IReadOnlyList<MusicInfo> FindForAdmin(string? query, int take) => MusicManager.Find(query, take);

    /// <summary>Admin: counts for the panel's overview.</summary>
    public object Summary() => MusicManager.Summary();

    /// <summary>Admin: rewrite one song's names and album.</summary>
    public Task<(MusicInfo? entry, string? error)> EditAsync(string id, IReadOnlyList<string>? titles,
        IReadOnlyList<string>? artists, string? album) => MusicManager.EditAsync(id, titles, artists, album);

    public Task<(MusicInfo? entry, string? error)> ImportAsync(string artist, string title, string? album,
        string extension, Stream content, byte[]? cover = null, CancellationToken cancellationToken = default) =>
        MusicManager.ImportAsync(artist, title, album, extension, content, cover, cancellationToken);

    /// <summary>The library entry behind an ID, with the fields <see cref="PlatformResult" /> does not carry.</summary>
    public MusicInfo? FindEntry(string id) => MusicManager.SearchById(id);

    /// <summary>stih: what it found, or that it found nothing.</summary>
    public Task<(MusicInfo? entry, string? error)> StampLyricsAsync(string id, LyricsKind? kind, LyricsOrigin? source)
        => MusicManager.StampLyricsAsync(id, kind, source);

    /// <summary>stih: the sweep's work list.</summary>
    public IReadOnlyList<MusicInfo> MissingLyrics(int take, DateOnly retryBefore) =>
        MusicManager.MissingLyrics(take, retryBefore);

    public (LocalMatch Match, PlatformResult Result)? FindLocalVariant(string name, string? artist, TimeSpan duration)
    {
        var match = MusicManager.FindLocalVariant(name, artist, duration);
        return match is null ? null : (match, match.Song.ToMusicResult(ContentDownloaders));
    }

    protected override void Initialize()
    {
        Logger.Debug("Initializing MusicSearchProvider");
        _ = MusicManager.Initialize();
        base.Initialize();
    }

    private IAsyncEnumerable<PlatformResult> ToResults(IEnumerable<MusicInfo> songs)
    {
        return songs.Select(PlatformResult (song) => song.ToMusicResult(ContentDownloaders)).AsAsync();
    }
}