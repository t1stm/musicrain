using Gaida.Core.Platforms;
using Gaida.Core.Platforms.Optional.Supports;
using Gaida.Core.Utils;
using Gaida.Platforms.MusicDatabase.Manager;
using Serilog;

namespace Gaida.Platforms.MusicDatabase.Search_Providers;

public class MusicSearchProvider(ILogger logger) : SearchProvider(logger),
    ISupportsId, ISupportsSearch, ISupportsRandomResults
{
    private readonly MusicManager _musicManager = new(logger);
    public override string PlatformIdentifier => "audio://";
    public override int Priority => 99;

    public Task<PlatformResult?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        var found = _musicManager.SearchById(id);
        return Task.FromResult<PlatformResult?>(found?.ToMusicResult(ContentDownloaders));
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
        return (folders, [.. files.Select(PlatformResult (song) => song.ToMusicResult(ContentDownloaders))]);
    }

    /// <summary>The library scan, awaitable — <see cref="Initialize" /> starts it and does not wait.</summary>
    public Task InitializeAsync() => _musicManager.Initialize();

    /// <summary>Admin: the library as rows to edit.</summary>
    public IReadOnlyList<MusicInfo> FindForAdmin(string? query, int take) => _musicManager.Find(query, take);

    /// <summary>Admin: counts for the panel's overview.</summary>
    public object Summary() => _musicManager.Summary();

    /// <summary>Admin: rewrite one song's names and album.</summary>
    public Task<(MusicInfo? entry, string? error)> EditAsync(string id, IReadOnlyList<string>? titles,
        IReadOnlyList<string>? artists, string? album) => _musicManager.EditAsync(id, titles, artists, album);

    public Task<(MusicInfo? entry, string? error)> ImportAsync(string artist, string title, string? album,
        string extension, Stream content, byte[]? cover = null, CancellationToken cancellationToken = default) =>
        _musicManager.ImportAsync(artist, title, album, extension, content, cover, cancellationToken);

    /// <summary>The library entry behind an ID, with the fields <see cref="PlatformResult" /> does not carry.</summary>
    public MusicInfo? FindEntry(string id) => _musicManager.SearchById(id);

    /// <summary>stih: what it found, or that it found nothing.</summary>
    public Task<(MusicInfo? entry, string? error)> StampLyricsAsync(string id, LyricsKind? kind, LyricsOrigin? source)
        => _musicManager.StampLyricsAsync(id, kind, source);

    /// <summary>stih: the sweep's work list.</summary>
    public IReadOnlyList<MusicInfo> MissingLyrics(int take, DateOnly retryBefore) =>
        _musicManager.MissingLyrics(take, retryBefore);

    public (LocalMatch Match, PlatformResult Result)? FindLocalVariant(string name, string? artist, TimeSpan duration)
    {
        var match = _musicManager.FindLocalVariant(name, artist, duration);
        return match is null ? null : (match, match.Song.ToMusicResult(ContentDownloaders));
    }

    protected override void Initialize()
    {
        Logger.Debug("Initializing MusicSearchProvider");
        _ = _musicManager.Initialize();
        base.Initialize();
    }

    private IAsyncEnumerable<PlatformResult> ToResults(IEnumerable<MusicInfo> songs)
    {
        return songs.Select(PlatformResult (song) => song.ToMusicResult(ContentDownloaders)).AsAsync();
    }
}
