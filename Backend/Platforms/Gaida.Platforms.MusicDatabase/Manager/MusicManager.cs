using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.RegularExpressions;
using Gaida.Core.Utils;
using Serilog;

namespace Gaida.Platforms.MusicDatabase.Manager;

[SuppressMessage("Microsoft.Design", "CA1001:TypesThatOwnDisposableFieldsShouldBeDisposable",
    Justification = "The edit gate is a SemaphoreSlim that never hands out its wait handle, so it holds nothing " +
                    "to release, and the library lives as long as the pod.")]
public partial class MusicManager(ILogger logger, LibraryOptions options)
{
    private readonly CoverExtractor _coverExtractor = new(logger, options.CoverDirectory);
    private readonly LibraryDatabase _database = new(options);

    /// <summary>
    ///     Serialises admin edits against each other and against their own file writes.
    /// </summary>
    /// <remarks>
    ///     ponytail: one gate for the whole library rather than one per folder. Edits arrive at the rate
    ///     a person clicks Save, and the work under it is a lookup plus one row written. Split it per
    ///     folder if a bulk re-tagging tool ever shows up.
    /// </remarks>
    private readonly SemaphoreSlim _editGate = new(1, 1);

    /// <summary>1 while a scan runs, so a second Rescan click is refused rather than queued behind it.</summary>
    private int _scanning;

    private List<MusicInfo> _songs = [];

    /// <summary>Replaced whole, never edited in place: readers walk it without a lock.</summary>
    protected List<MusicInfo> Songs
    {
        get => Volatile.Read(ref _songs);
        set => Volatile.Write(ref _songs, value);
    }

    private ILogger Logger { get; } = logger;

    /// <summary>
    ///     The current tag-reading pass. Bump it when the scanner learns to read a tag it did not before:
    ///     every entry stamped below this is re-read once on the next load, and stamped. Pass 1 is the
    ///     album, which <see cref="MediaInfo" /> never asked ffprobe for. Pass 2 is which name leads, and
    ///     pass 3 writes a shared credit with commas — see <see cref="MusicInfo.AddNames" />. Pass 4 is ATL
    ///     in place of ffprobe, which reads what ffprobe missed: an Ogg's Vorbis comments, and a VBR MP3's
    ///     real length. Pass 5 decodes the cp1251 tags ATL reads as Latin-1 — see <see cref="MediaInfo.Recovered" />.
    /// </summary>
    public const int ScanVersion = 5;

    /// <returns>Matching songs, empty when the term is unusable or nothing matches.</returns>
    public IEnumerable<MusicInfo> SearchByTerm(string term)
    {
        Logger.Debug("MusicManager: Searching by term: {Term}", term);
        var termClean = LevenshteinDistance.RemoveFormatting(
            ParentesisRegex().Replace(term, string.Empty));

        if (string.IsNullOrEmpty(termClean))
        {
            Logger.Information("MusicManager: Cleaned search term is empty for: {Term}", term);
            return [];
        }

        var found = Songs.Where(r => ScoreSingleTerm(termClean, r)).ToList();
        Logger.Debug("MusicManager: Found {Count} matches for term: {Term}", found.Count, term);
        return found;
    }

    public IEnumerable<MusicInfo> GetRandomSongs(int count)
    {
        Logger.Debug("MusicManager: Getting {Count} random songs", count);
        var songs = Songs.ToArray();
        Random.Shared.Shuffle(songs);
        return songs.Take(count);
    }

    /// <summary>The edit distance a search term must come in under against one name, and against title and artist.</summary>
    private const int NameEdits = 2, PairEdits = 3;

    /// <summary>The most entries one page of the admin list or the lyrics backlog holds.</summary>
    private const int MaxPage = 500;

    /// <summary>Every title against every artist, in both orders — the arrays are what the entry can be found by.</summary>
    private static bool ScoreSingleTerm(string termClean, MusicInfo r)
    {
        var (titles, artists, _) = r.Search;

        return artists.Any(artist => LevenshteinDistance.ComputeStrict(artist, termClean) < NameEdits)
               || titles.Any(title => LevenshteinDistance.ComputeStrict(title, termClean) < NameEdits)
               || titles.Any(title => artists.Any(artist =>
                   LevenshteinDistance.ComputeStrict(title + artist, termClean) < PairEdits ||
                   LevenshteinDistance.ComputeStrict(artist + title, termClean) < PairEdits));
    }

    /// <returns>The song, or <c>null</c> when the ID isn't known.</returns>
    public MusicInfo? SearchById(string id)
    {
        Logger.Debug("MusicManager: Searching by ID: {ID}", id);
        var search = Songs.AsParallel().FirstOrDefault(r => r.Id == id);

        // Second pass for regenerated infos, whose last two characters are re-rolled.
        if (search is null && id.Length > 2)
            search = Songs.AsParallel().FirstOrDefault(r => r.Id?.Length > 2 && r.Id[..^2] == id[..^2]);

        if (search is null)
        {
            Logger.Information("MusicManager: ID not found: {ID}", id);
            return null;
        }

        Logger.Debug("MusicManager: Found song for ID {ID}: {Title}", id, search.Title);
        return search;
    }

    /// <summary>
    ///     The library as the folder tree it already is on disk: one level of it, so the client asks for the
    ///     next level only when someone opens it.
    /// </summary>
    /// <param name="path">Folder relative to the storage root; empty or "/" for the root itself.</param>
    /// <returns>The immediate subfolders with the songs beneath each, and the songs directly in the folder.</returns>
    public (List<(string Name, int Songs)> Folders, List<MusicInfo> Files) Browse(string? path)
    {
        // Nothing here touches the filesystem: the path is only ever compared against the RelativeLocation
        // strings already in memory, so "../" matches no prefix and escapes nothing. Separators are '/'
        // because ParseFile splits on '/' too — a backslash is normalized rather than supported.
        var prefix = FolderPath.Normalize(path);
        if (prefix.Length > 0) prefix += "/";

        var folders = new Dictionary<string, int>(StringComparer.Ordinal);
        var files = new List<MusicInfo>();

        foreach (var song in Songs)
        {
            if (song.RelativeLocation is not { } location ||
                !location.StartsWith(prefix, StringComparison.Ordinal)) continue;

            var rest = location[prefix.Length..];
            var slash = rest.IndexOf('/');

            if (slash < 0) files.Add(song);
            else folders[rest[..slash]] = folders.GetValueOrDefault(rest[..slash]) + 1;
        }

        Logger.Debug("MusicManager: Browsed '{Path}': {Folders} folders, {Files} files", prefix, folders.Count,
            files.Count);

        // ponytail: one linear scan of the song list per request. 3671 entries is sub-millisecond and a prefix
        // index would need invalidating on every rescan — build one only if the library passes six figures.
        return ([
                .. folders.OrderBy(folder => folder.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(folder => (Name: folder.Key, Songs: folder.Value))
            ],
            [
                .. files.OrderBy(song => song.DisplayTitle, StringComparer.OrdinalIgnoreCase)
            ]);
    }

    /// <summary>
    ///     Every song beneath <paramref name="path" />, in the order the explorer lists them with every folder
    ///     opened: each level's subfolders first, then its files. Walked through <see cref="Browse" />, so the
    ///     order and the path handling are the page's by construction rather than by a second copy of them.
    /// </summary>
    /// <remarks>
    ///     ponytail: one <see cref="Browse" /> scan per folder, so songs × folders — a few milliseconds for a
    ///     library of thousands. One prefix filter and a segment-wise comparer if it ever shows in a trace.
    /// </remarks>
    public IEnumerable<MusicInfo> BrowseTracks(string? path)
    {
        var folder = FolderPath.Normalize(path);
        var (folders, files) = Browse(folder);

        foreach (var child in folders)
            foreach (var song in BrowseTracks(folder.Length == 0 ? child.Name : $"{folder}/{child.Name}"))
                yield return song;

        foreach (var song in files) yield return song;
    }

    /// <summary>
    ///     Plain substring matching over every variant, the album and the path, for the admin editor.
    /// </summary>
    /// <remarks>
    ///     Deliberately not <see cref="SearchByTerm" />: that one is tuned for a listener who half
    ///     remembers a title, and its fuzziness is wrong here. An operator fixing "Оркестър Имперал"
    ///     needs to find that exact typo, and a search that helpfully also returns the correctly spelled
    ///     song is a search that hides the thing being looked for.
    /// </remarks>
    public IReadOnlyList<MusicInfo> Find(string? query, int take)
    {
        var needle = query?.Trim() ?? string.Empty;
        var songs = Songs;

        var matched = needle.Length == 0
            ? songs.AsEnumerable()
            : songs.Where(song =>
                song.Titles.Any(title => title.Contains(needle, StringComparison.OrdinalIgnoreCase))
                || song.Artists.Any(artist => artist.Contains(needle, StringComparison.OrdinalIgnoreCase))
                || song.Album?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true
                || song.RelativeLocation?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true
                || song.Id == needle);

        return [.. matched
            .OrderBy(song => song.Artist, StringComparer.OrdinalIgnoreCase)
            .ThenBy(song => song.Title, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(take, 1, MaxPage))];
    }

    /// <summary>Counts for the admin panel's overview. One pass, no allocation per song.</summary>
    public object Summary()
    {
        var songs = Songs;
        var folders = new HashSet<string>(StringComparer.Ordinal);
        var withoutAlbum = 0;
        var withoutArtist = 0;
        var withLyrics = 0;
        var synchronized = 0;

        foreach (var song in songs)
        {
            if (song.RelativeLocation is { } location) folders.Add(LibraryDatabase.FolderOf(location));
            if (string.IsNullOrWhiteSpace(song.Album)) withoutAlbum++;
            if (song.Artists.Count == 0) withoutArtist++;
            if (song.LyricsType is not null) withLyrics++;
            if (song.LyricsType == LyricsKind.Synchronized) synchronized++;
        }

        return new
        {
            service = "gaida-local",
            songs = songs.Count,
            folders = folders.Count,
            withoutAlbum,
            withoutArtist,
            withLyrics,
            synchronized,
            scanning = Volatile.Read(ref _scanning) == 1,
            storage = options.StorageDirectory
        };
    }

    [GeneratedRegex(@"\(.*?\)")]
    private static partial Regex ParentesisRegex();

    private static bool IsArtistPartOfSong(string artist, MusicInfo song)
    {
        return song.Search.Artists.Any(name => name.Contains(artist, StringComparison.Ordinal));
    }

    /// <returns>The artist's songs, empty when the name is unusable or nothing matches.</returns>
    public IEnumerable<MusicInfo> GetArtistSongs(string artist)
    {
        Logger.Debug("MusicManager: Getting songs for artist: {Artist}", artist);
        var artistRemovedFormatting = LevenshteinDistance.RemoveFormatting(artist);
        if (string.IsNullOrEmpty(artistRemovedFormatting))
        {
            Logger.Information("MusicManager: Cleaned artist name is empty for: {Artist}", artist);
            return [];
        }

        var artistSongs = Songs.AsParallel()
            .Where(song => IsArtistPartOfSong(artistRemovedFormatting, song)).ToList();

        Logger.Debug("MusicManager: Found {Count} songs for artist: {Artist}", artistSongs.Count, artist);
        return artistSongs;
    }

    /// <summary>The tracks of one album, in the order its playlist file gives them.</summary>
    /// <remarks>
    ///     An album is a <c>&lt;name&gt;.m3u</c>/<c>.m3u8</c> in the artist's folder, not an Album tag —
    ///     every song has a tag, only an assembled album has a file, and only the file knows the running
    ///     order, since nothing in the tags holds a track number. An album tag with no playlist file beside
    ///     it returns nothing here, and Gaida.API falls through to Deezer for it.
    /// </remarks>
    /// <returns>The album's songs in playlist order, empty when either name is unusable or nothing matches.</returns>
    public IEnumerable<MusicInfo> GetAlbumSongs(string artist, string album)
    {
        var wanted = album.Trim();
        if (wanted.Length == 0) return [];

        // The artist picks the folder; the playlist picks the tracks. Distinct because an artist can sit
        // under more than one folder — a second genre, a compilation.
        foreach (var folder in GetArtistSongs(artist)
                     .Select(song => song.RelativeLocation is { } location
                         ? LibraryDatabase.FolderOf(location)
                         : string.Empty)
                     .Where(folder => folder.Length > 0).Distinct(StringComparer.Ordinal))
        {
            if (PlaylistFor(folder, wanted) is not { } playlist) continue;

            Logger.Debug("MusicManager: Album '{Album}' is {Playlist}", wanted, playlist);
            return Ordered(SongsIn(folder), File.ReadLines(playlist));
        }

        Logger.Information("MusicManager: No playlist file for album '{Album}' by '{Artist}'", wanted, artist);
        return [];
    }

    /// <returns>The album's playlist file in <paramref name="folder" />, or <c>null</c> when it has none.</returns>
    /// <remarks>
    ///     ponytail: one directory listing and one small file read per request, on a query path that
    ///     otherwise never touches the disk. An album is tens of lines and the page is opened by hand.
    ///     Index the playlists at <see cref="Load" /> if album pages ever get hot — the cost of that is
    ///     invalidating the index on every rescan.
    /// </remarks>
    private string? PlaylistFor(string folder, string album)
    {
        var directory = Path.Combine(options.StorageDirectory, folder);
        if (!Directory.Exists(directory)) return null;

        // The album name comes from a client, so it is never built into a path: the folder's playlists are
        // listed and the names compared in memory instead. A "../" album reaches nothing by construction
        // rather than by a check someone has to remember. The glob is the cheap half of the filter — the
        // extension is checked properly below, since "*.m3u*" also matches names that only start that way.
        return Directory.EnumerateFiles(directory, "*.m3u*").FirstOrDefault(file =>
            (file.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase) ||
             file.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)) &&
            string.Equals(Path.GetFileNameWithoutExtension(file), album, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     The folder's songs in the playlist's order. Comments (<c>#EXTM3U</c>, <c>#EXTINF</c>) and lines
    ///     the library does not have — a <c>.wvc</c> correction file, a track since deleted — are skipped
    ///     rather than faked into rows that play nothing.
    /// </summary>
    /// <remarks>
    ///     Only the filename of each line is used. An album playlist lists its own folder's files, so the
    ///     name is all there is to match on, and taking just the name means a <c>./</c> prefix costs
    ///     nothing and a <c>../</c> reaches nowhere. Internal rather than private: this is the part worth
    ///     a test, and taking lines rather than a path is what lets that test run without a library.
    /// </remarks>
    internal static IEnumerable<MusicInfo> Ordered(IEnumerable<MusicInfo> folderSongs, IEnumerable<string> lines)
    {
        var byName = new Dictionary<string, MusicInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var song in folderSongs)
            if (song.RelativeLocation is { } location)
                byName.TryAdd(Path.GetFileName(location.Replace('\\', '/')), song);

        foreach (var line in lines)
        {
            var entry = line.Trim();
            if (entry.Length == 0 || entry[0] == '#') continue;

            if (byName.TryGetValue(Path.GetFileName(entry.Replace('\\', '/')), out var song)) yield return song;
        }
    }

    /// <summary>
    ///     Every song in one folder — not just the searched artist's, so a track the playlist lists under a
    ///     different credit is still found. The artist match only ever chooses the folder.
    /// </summary>
    private IEnumerable<MusicInfo> SongsIn(string folder)
    {
        return Songs.Where(song =>
            song.RelativeLocation is { } location && LibraryDatabase.FolderOf(location) == folder);
    }
}
