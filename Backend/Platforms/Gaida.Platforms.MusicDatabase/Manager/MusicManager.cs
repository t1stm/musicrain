using System.Collections.Concurrent;
using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Gaida.Core.Utils;
using Gaida.Sqlite;
using Microsoft.Data.Sqlite;
using Serilog;

namespace Gaida.Platforms.MusicDatabase.Manager;

public partial class MusicManager(ILogger logger)
{
    private readonly CoverExtractor _coverExtractor = new();

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

    protected List<MusicInfo> Songs = [];
    private ILogger Logger { get; } = logger;

    public static string Domain =>
        Environment.GetEnvironmentVariable("DOMAIN", EnvironmentVariableTarget.Process) ?? string.Empty;

    public static string StorageDirectory =>
        Environment.GetEnvironmentVariable("STORAGE", EnvironmentVariableTarget.Process) ?? "./";

    // TrimEnd because DOMAIN is written with a trailing slash as often as not, and concatenating it
    // straight onto "/Album_Covers" produced "https://host//Album_Covers/<hash>.jpg" — a URL that
    // renders in a browser but that Cover.cs's HttpClient fetch and any strict client both choke on.
    public static string AlbumCoverLocation => Domain.TrimEnd('/') + "/Album_Covers";

    /// <summary>
    ///     The current tag-reading pass. Bump it when the scanner learns to read a tag it did not before:
    ///     every entry stamped below this is re-read once on the next load, and stamped. Pass 1 is the
    ///     album, which <see cref="MediaInfo" /> never asked ffprobe for. Pass 2 is which name leads, and
    ///     pass 3 writes a shared credit with commas — see <see cref="MusicInfo.AddNames" />. Pass 4 is ATL
    ///     in place of ffprobe, which reads what ffprobe missed: an Ogg's Vorbis comments, and a VBR MP3's
    ///     real length.
    /// </summary>
    public const int ScanVersion = 4;

    public async Task Initialize()
    {
        Logger.Information("Initializing MusicManager");
        var storage = Environment.GetEnvironmentVariable("STORAGE", EnvironmentVariableTarget.Process);
        if (storage is not null)
        {
            Logger.Debug("Ensuring storage directory exists: {Storage}", storage);
            Directory.CreateDirectory(storage);
        }

        var albumCovers = Environment.GetEnvironmentVariable("ALBUM_COVERS", EnvironmentVariableTarget.Process);
        if (albumCovers is not null)
        {
            Logger.Debug("Ensuring album covers directory exists: {AlbumCovers}", albumCovers);
            Directory.CreateDirectory(albumCovers);
        }

        _scanning = 1;
        await Scan();
        Logger.Information("MusicManager initialization complete. Loaded {Count} songs", Songs.Count);
    }

    /// <summary>
    ///     Starts the boot scan again, for files dropped into the library while the pod is up. Returns without
    ///     waiting: a library's worth of new files outlasts Oko's action timeout. The snapshot's
    ///     <c>scanning</c> flag is how the panel sees it finish.
    /// </summary>
    /// <returns><c>false</c> when a scan is already running.</returns>
    public bool TryRescan()
    {
        if (Interlocked.CompareExchange(ref _scanning, 1, 0) != 0) return false;

        _ = Task.Run(async () =>
        {
            try
            {
                await Scan();
                Logger.Information("Rescan complete. {Count} songs", Songs.Count);
            }
            catch (Exception exception)
            {
                Logger.Error(exception, "Rescan failed");
            }
        });

        return true;
    }

    /// <summary>
    ///     The whole library off disk. Under the edit gate, because both halves write rows built from what
    ///     they read a moment earlier: an edit, import or lyrics stamp landing in between would be written
    ///     over with the row as it was before it.
    /// </summary>
    private async Task Scan()
    {
        await _editGate.WaitAsync();

        try
        {
            await Load();
            Logger.Debug("Extracting covers from {StorageDirectory}", StorageDirectory);
            ExtractCovers();
        }
        finally
        {
            _editGate.Release();
            Volatile.Write(ref _scanning, 0);
        }
    }

    protected async Task Load()
    {
        Logger.Debug("Loading music from {StorageDirectory}", StorageDirectory);
        var folders = Directory.EnumerateDirectories(StorageDirectory, "*", SearchOption.AllDirectories).ToList();
        Logger.Debug("Found {Count} folders in storage", folders.Count);

        var stored = ReadLibrary();
        var parsed = new ConcurrentBag<(string Folder, List<MusicInfo> Entries, bool Changed)>();
        await Parallel.ForEachAsync(folders, async (folder, _) =>
        {
            var relative = Path.GetRelativePath(StorageDirectory, folder).Replace('\\', '/');
            var (entries, changed) = await ParseArtistFolder(folder, stored.GetValueOrDefault(relative) ?? []);
            parsed.Add((relative, entries, changed));
        });

        // After the parallel pass rather than inside it, so the scan is one writer and one transaction. Before
        // the $[DOMAIN] substitution below, so the rows keep the placeholder.
        var changed = parsed.Where(folder => folder.Changed).ToList();
        if (changed.Count > 0)
        {
            Write(changed.SelectMany(folder => folder.Entries), changed.Select(folder => folder.Folder));
            Logger.Information("Wrote {Count} folder(s) to {Library}", changed.Count, LibraryDatabase);
        }

        // ponytail: folder order is no longer stable; nothing downstream depends on it (search scores, random shuffles).
        var songs = parsed.SelectMany(f => f.Entries).ToList();

        // An entry whose file is gone -- converted to another format beside it, moved, deleted by hand -- is
        // a search hit that answers every play with a 404. It stays in library.db, ID and all, and comes
        // back at the next boot if the file does.
        var missing = songs.RemoveAll(song => !File.Exists(StorageDirectory + "/" + song.RelativeLocation));
        if (missing > 0) Logger.Warning("Left out {Count} entries whose file is gone", missing);

        songs.ForEach(s => s.CoverUrl = s.CoverUrl?.Replace("$[DOMAIN]", AlbumCoverLocation));

        lock (Songs)
        {
            Songs = songs;
        }
    }

    /// <param name="artist">The folder, as an absolute path.</param>
    /// <param name="stored">Its rows in <c>library.db</c>; empty for a folder the database has never seen.</param>
    /// <returns>The folder's entries, and whether they differ from the rows.</returns>
    private async Task<(List<MusicInfo> Entries, bool Changed)> ParseArtistFolder(string artist,
        List<MusicInfo> stored)
    {
        Logger.Information("Loading artist: '{Artist}'", artist);

        var songs = Directory.GetFiles(artist, "*", SearchOption.TopDirectoryOnly)
            .Where(song => IsAudioBasedOnFileExtension(song)).ToList();

        // A folder the database has never seen starts from the Info.json older versions kept beside its
        // audio, through every pass below exactly as that version would have read it. Its rows are written
        // at the end of the scan, and from then on the file is never read again — nor written, so an older
        // image pointed at this library still finds the folder as it was.
        var existing = stored;
        var imported = false;
        if (existing.Count == 0 && await ReadInfoJson(artist) is { Count: > 0 } fromFile)
        {
            existing = fromFile;
            imported = true;
        }

        // Folders that only hold subfolders have nothing to index.
        if (songs.Count == 0 && existing.Count == 0) return ([], false);

        // Stale entries (e.g. .wvc WavPack correction files indexed by an older scanner) are never playable.
        var stale = existing.RemoveAll(m => m.RelativeLocation is null ||
                                            !IsAudioBasedOnFileExtension(m.RelativeLocation));
        if (stale > 0) Logger.Information("Dropped {Count} non-audio entries for '{Artist}'", stale, artist);

        // The four-field format took its names from the path, because the tags were read case-sensitively
        // and only .mp3 arrives lowercased. NewFiles never revisits an indexed song, so the re-read has to
        // happen here or those names stay path-derived forever.
        var legacy = existing.Where(entry => entry.WasLegacy).ToList();
        foreach (var entry in legacy) RereadTags(entry);
        if (legacy.Count > 0)
            Logger.Information("Re-read tags for {Count} entries of '{Artist}'", legacy.Count, artist);

        // Passes the scanner had not learned when these entries were indexed. Unlike the legacy re-read this
        // must not re-roll the ID, so it goes through Backfill rather than RereadTags.
        var behind = existing.Where(entry => entry.Scan < ScanVersion).ToList();
        foreach (var entry in behind)
        {
            Backfill(entry);
            entry.Scan = ScanVersion;
        }

        if (behind.Count > 0)
            Logger.Information("Backfilled {Count} entries of '{Artist}' to scan {Version}", behind.Count, artist,
                ScanVersion);

        // The lyrics sidecars, every scan rather than once behind a ScanVersion bump: it is two
        // File.Exists per song against a version bump that would re-read every file in the library,
        // and it is what lets a .lrc deleted by hand disappear from the index at the next boot.
        var reconciled = existing.Count(ReconcileLyrics);
        if (reconciled > 0)
            Logger.Information("Reconciled lyrics on {Count} entries of '{Artist}'", reconciled, artist);

        var newFiles = NewFiles(existing, songs).ToList();
        foreach (var file in newFiles)
            existing.Add(ParseFile(file));

        return (existing, imported || stale > 0 || newFiles.Count > 0 || legacy.Count > 0 || behind.Count > 0 ||
                          reconciled > 0);
    }

    /// <summary>The Info.json an older version kept in <paramref name="folder" />, or empty without one.</summary>
    private async Task<List<MusicInfo>> ReadInfoJson(string folder)
    {
        var file = Path.Combine(folder, "Info.json");
        if (!File.Exists(file)) return [];

        try
        {
            await using var stream = File.OpenRead(file);
            return await JsonSerializer.DeserializeAsync<List<MusicInfo>>(stream, MusicInfo.SerializerOptions) ?? [];
        }
        catch (JsonException e)
        {
            Logger.Fatal(e, "Malformed Info.json for '{Artist}', rebuilding it", folder);
            return [];
        }
    }

    /// <summary>
    ///     Embedded artwork for every song that has none yet: written to the covers directory, recorded on
    ///     the entry in memory, and its row rewritten. Content-addressed, so a song whose file carries no
    ///     picture costs one read per scan and nothing else.
    /// </summary>
    private void ExtractCovers()
    {
        var covered = new ConcurrentBag<MusicInfo>();
        Parallel.ForEach(Songs.Where(song => string.IsNullOrWhiteSpace(song.CoverUrl)), song =>
        {
            if (_coverExtractor.ExportCover(StorageDirectory + "/" + song.RelativeLocation) is not { } cover) return;

            // The substituted form, as Load leaves every other entry; MusicInfo.StoredCoverUrl puts the
            // placeholder back on the way into the row.
            song.CoverUrl = $"{AlbumCoverLocation}/{cover}";
            covered.Add(song);
        });

        if (covered.IsEmpty) return;

        Write(covered);
        Logger.Information("Extracted {Count} cover(s)", covered.Count);
    }

    private static void RereadTags(MusicInfo entry)
    {
        var path = StorageDirectory + "/" + entry.RelativeLocation;
        if (!File.Exists(path)) return;

        var tagged = MediaInfo.GetInformation(path);
        entry.PreferTags(tagged);
        entry.Id = entry.UpdateRandomId();

        // The pipe deadlock ffprobe once had left a couple of entries with no duration at all, and the weak
        // match gates on it. The re-read is the one place that can repair them.
        if (entry.Duration == TimeSpan.Zero) entry.Duration = tagged.Duration;
    }

    /// <summary>
    ///     Brings an entry up from the pass that indexed it. Never touches the ID: playlists, cache keys and
    ///     recently-played lists hold it, and <see cref="MusicInfo.UpdateRandomId" /> ends in a random suffix.
    /// </summary>
    /// <remarks>
    ///     Every pass, whatever the entry is stamped with: pass 4 changed the reader under the first three, so
    ///     they all run again over what it reads.
    /// </remarks>
    private static void Backfill(MusicInfo entry)
    {
        var path = StorageDirectory + "/" + entry.RelativeLocation;
        if (!File.Exists(path)) return;

        var fresh = MediaInfo.GetInformation(path);

        // Pass 1. An album an admin typed outranks the file, so this only fills a missing one.
        entry.Album ??= fresh.Album;

        // Passes 2 and 3, both nothing but AddNames run again.
        var (title, author, folder) = PathNames(path);
        fresh.AddNames(title, author, folder);
        entry.Rederive(fresh);

        // Pass 4. ffprobe guessed a VBR MP3 with no Xing header from its bitrate: 326 s for a 140 s track,
        // which is the length the weak match gates on.
        if (fresh.Duration > TimeSpan.Zero) entry.Duration = fresh.Duration;

        // Also pass 4: a cover named by the SHA-1 of nothing is the zero-byte file an empty TagLib# picture
        // became. Dropped, so the cover pass after the load extracts the real one through ATL.
        if (entry.CoverUrl?.EndsWith("/da39a3ee5e6b4b0d3255bfef95601890afd80709.") == true) entry.CoverUrl = null;
    }

    private static IEnumerable<string> NewFiles(List<MusicInfo> existing, List<string> files)
    {
        return files.Where(location =>
            existing.All(m => m.RelativeLocation != RelativeLocation(location)));
    }

    private static string RelativeLocation(string location)
    {
        return Path.GetRelativePath(StorageDirectory, location);
    }

    /// <summary>
    ///     <c>Genre/Folder/Author - Title.ext</c>. A title with its own " - " keeps it: only the first one
    ///     separates the author.
    /// </summary>
    internal static (string Title, string Author, string Folder) PathNames(string location)
    {
        var split = location.Split('/');
        var filenameSplit = split[^1].Split(" - ");

        return (Path.GetFileNameWithoutExtension(string.Join(" - ", filenameSplit[1..])), filenameSplit[0],
            split.Length > 1 ? split[^2] : string.Empty);
    }

    private static MusicInfo ParseFile(string location)
    {
        var (title, author, folder) = PathNames(location);

        // The path spellings are kept as alternates rather than discarded, so a folder typo costs a variant
        // instead of the whole name. AddNames decides whether the tag or the filename leads.
        var entry = MediaInfo.GetInformation(location);
        entry.AddNames(title, author, folder);
        entry.RelativeLocation ??= RelativeLocation(location);
        entry.Id = entry.UpdateRandomId();
        entry.Scan = ScanVersion;
        ReconcileLyrics(entry);

        return entry;
    }

    private static bool IsAudioBasedOnFileExtension(ReadOnlySpan<char> fileName)
    {
        return fileName.EndsWith(".flac") || fileName.EndsWith(".ogg") ||
               fileName.EndsWith(".mp3") || fileName.EndsWith(".wav") ||
               fileName.EndsWith(".mka") || fileName.EndsWith(".adts") ||
               fileName.EndsWith(".wma") || fileName.EndsWith(".wv");
    }

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

    /// <summary>Every title against every artist, in both orders — the arrays are what the entry can be found by.</summary>
    private static bool ScoreSingleTerm(string termClean, MusicInfo r)
    {
        var (titles, artists, _) = r.Search;

        return artists.Any(artist => LevenshteinDistance.ComputeStrict(artist, termClean) < 2)
               || titles.Any(title => LevenshteinDistance.ComputeStrict(title, termClean) < 2)
               || titles.Any(title => artists.Any(artist =>
                   LevenshteinDistance.ComputeStrict(title + artist, termClean) < 3 ||
                   LevenshteinDistance.ComputeStrict(artist + title, termClean) < 3));
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
        var prefix = (path ?? string.Empty).Replace('\\', '/').Trim('/');
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
        var folder = (path ?? string.Empty).Replace('\\', '/').Trim('/');
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
            .Take(Math.Clamp(take, 1, 500))];
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
            if (song.RelativeLocation is { } location) folders.Add(FolderOf(location));
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
            storage = StorageDirectory
        };
    }

    /// <summary>
    ///     Rewrites one song's names and album, and saves the folder it lives in.
    /// </summary>
    /// <param name="titles">Replaces every title variant. <c>null</c> leaves them alone; empty is rejected.</param>
    /// <param name="artists">Replaces every artist variant. <c>null</c> leaves them alone.</param>
    /// <param name="album">Trimmed; the empty string clears it, <c>null</c> leaves it alone.</param>
    /// <returns>The updated entry, or an error naming what was wrong with the request.</returns>
    /// <remarks>
    ///     What an operator types is taken literally: unlike the import path, this does not add
    ///     romanizations of its own. They are welcome as extra variants, but a person editing a name is
    ///     the authority on it and should not find a line they never wrote appearing underneath.
    ///     <para>
    ///         The ID is deliberately <b>not</b> regenerated, though it is derived from these very
    ///         fields. It is the handle every playlist snapshot, every Dunav cache key and every link
    ///         already holds; re-rolling it on a typo fix would orphan all of them. <c>RereadTags</c>
    ///         regenerates because a bulk migration has no such links to keep.
    ///     </para>
    /// </remarks>
    /// <param name="id">The entry to edit. Kept as-is — see the remark on regeneration.</param>
    public async Task<(MusicInfo? entry, string? error)> EditAsync(string id, IReadOnlyList<string>? titles,
        IReadOnlyList<string>? artists, string? album)
    {
        await _editGate.WaitAsync();

        try
        {
            var entry = SearchById(id);
            if (entry is null) return (null, "No song with that ID.");
            if (entry.RelativeLocation is null) return (null, "That entry has no file on disk.");

            if (titles is not null)
            {
                var cleaned = Distinct(titles);
                if (cleaned.Count == 0) return (null, "A song needs at least one title.");
                entry.Titles = cleaned;
            }

            if (artists is not null) entry.Artists = Distinct(artists);
            if (album is not null) entry.Album = album.Trim() is { Length: > 0 } name ? name : null;

            Write([entry]);
            Logger.Information("Admin edited {ID}: {Title} — {Artist}", id, entry.Title, entry.Artist);

            return (entry, null);
        }
        finally
        {
            _editGate.Release();
        }
    }

    /// <summary>The top-level folder every imported track lands under, relative to the storage root.</summary>
    /// <remarks>
    ///     It sits where a genre sits in the rest of the tree — <c>Eurobeat/Lou Grant/Lou Grant - ....wv</c>
    ///     — because that is what the library is organised by and "where it came from" is the only honest
    ///     answer available at import time. An operator moving the artist folder under a real genre is a
    ///     move, not a re-import.
    /// </remarks>
    private const string ImportFolder = "Deezer";

    /// <summary>The name an artist folder gets when the source gave no usable artist at all.</summary>
    private const string UnknownArtistFolder = "Unknown artist";

    /// <summary>
    ///     Writes one downloaded track under <see cref="ImportFolder" /> and indexes it, so it becomes an
    ///     ordinary <c>audio://</c> library song.
    /// </summary>
    /// <remarks>
    ///     The layout is the library's own, one level shallower: <c>Deezer/&lt;artist&gt;/&lt;artist&gt; -
    ///     &lt;title&gt;.&lt;ext&gt;</c>. No album folder, because nothing in this tree has one — the level
    ///     between genre and artist that some entries carry is a sub-genre, and the album lives in the tags.
    ///     Putting the artist in its own folder is not cosmetic: <see cref="ParseFile" /> reads the
    ///     containing folder as an artist variant, so a file sitting directly in <c>Deezer/</c> is indexed
    ///     with "Deezer" as one of its artists.
    ///     <para>
    ///         Nothing else is guessed: the file is parsed by exactly the code a scan would have used, so an
    ///         import and a file dropped in by hand produce the same entry. The names the source supplied
    ///         are only the fallback, for a download whose tags say nothing.
    ///     </para>
    ///     <para>
    ///         An existing file of the same name is refused rather than overwritten. The admin is meant to
    ///         tidy these entries afterwards, and silently replacing a file underneath an entry someone had
    ///         already renamed is the one outcome that cannot be undone from the editor.
    ///     </para>
    /// </remarks>
    /// <param name="artist">The performer, as the entry's folder and artist list will read.</param>
    /// <param name="title">The track title, as the entry and its filename will read.</param>
    /// <param name="album">The album, or <c>null</c> when the source names none.</param>
    /// <param name="extension">Including the dot; must be one this library plays.</param>
    /// <param name="content">The audio itself, read to the end.</param>
    /// <param name="cover">
    ///     Artwork to fall back on when the file carries none embedded — a Deezer FLAC usually does not.
    ///     Stored and hashed exactly like an extracted cover, so the library holds its own copy.
    /// </param>
    /// <param name="cancellationToken">Cancels the read and the import.</param>
    /// <returns>The indexed entry, or an error naming what stopped it.</returns>
    public async Task<(MusicInfo? entry, string? error)> ImportAsync(string artist, string title, string? album,
        string extension, Stream content, byte[]? cover = null, CancellationToken cancellationToken = default)
    {
        var cleanArtist = CleanForFilename(artist);
        var cleanTitle = CleanForFilename(title);
        if (cleanTitle.Length == 0) return (null, "The track has no usable title.");
        if (!IsAudioBasedOnFileExtension(extension)) return (null, $"'{extension}' is not a playable extension.");

        var folder = cleanArtist.Length == 0 ? UnknownArtistFolder : cleanArtist;
        var directory = Path.Combine(StorageDirectory, ImportFolder, folder);
        var filename = $"{folder} - {cleanTitle}{extension}";
        var location = $"{directory}/{filename}";

        await _editGate.WaitAsync(cancellationToken);

        try
        {
            Directory.CreateDirectory(directory);
            if (File.Exists(location))
                return (null, $"'{filename}' is already in the {ImportFolder} folder.");

            // Temp then move: a download that dies halfway must not leave a truncated file behind, because
            // the next scan would index it as a whole song.
            var temporary = location + ".part";
            await using (var file = File.Create(temporary))
            {
                await content.CopyToAsync(file, cancellationToken);
            }

            File.Move(temporary, location);

            var entry = ParseFile(location);
            entry.Album ??= album?.Trim() is { Length: > 0 } named ? named : null;

            // The file's own artwork first, the source's only when it has none. The substituted form,
            // not the $[DOMAIN] placeholder: this entry is going straight into the in-memory library,
            // and MusicInfo.StoredCoverUrl puts the placeholder back on the way to disk.
            var artwork = _coverExtractor.ExportCover(location) ??
                          (cover is { Length: > 0 } ? _coverExtractor.StoreCover(cover) : null);
            if (artwork is not null) entry.CoverUrl = $"{AlbumCoverLocation}/{artwork}";

            // Copy-on-write rather than Add: SearchById and Browse read this list from other threads and
            // from AsParallel, and growing it underneath them is the classic torn-enumeration crash.
            Songs = [.. Songs, entry];
            Write([entry]);

            Logger.Information("Imported {Title} - {Artist} as {Location}", entry.Title, entry.Artist, location);
            return (entry, null);
        }
        finally
        {
            _editGate.Release();
        }
    }

    /// <summary>
    ///     A name that is safe as a filename and still round-trips through <see cref="ParseFile" />: it splits
    ///     on '/' for the folder and on " - " for the artist, so neither may survive into the name.
    /// </summary>
    private static string CleanForFilename(string? value)
    {
        var cleaned = new string((value ?? string.Empty)
            .Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)
            .ToArray());

        return cleaned.Replace(" - ", " ").Replace('/', '_').Replace('\\', '_').Trim().Trim('.');
    }

    /// <summary>Trimmed, blanks dropped, duplicates dropped, order preserved. Exactly what was typed.</summary>
    private static List<string> Distinct(IReadOnlyList<string> values)
    {
        var result = new List<string>(values.Count);
        foreach (var value in values)
        {
            var trimmed = value.Trim();
            if (trimmed.Length > 0 && !result.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                result.Add(trimmed);
        }

        return result;
    }

    /// <summary><c>library.db</c>, at the root of the library it indexes.</summary>
    private static string LibraryDatabase => Path.Combine(StorageDirectory, "library.db");

    /// <summary>
    ///     A connection to <see cref="LibraryDatabase" />, with its tables made if this is the first. Every
    ///     time rather than once, because <c>STORAGE</c> is read every time too, and the check is one pragma.
    /// </summary>
    private static SqliteConnection Open()
    {
        var db = Database.Open(Database.At(LibraryDatabase));
        Database.Upgrade(db, SchemaVersion, transaction => db.Execute(Schema, transaction: transaction));
        return db;
    }

    /// <summary>Every row, by the folder it sits in.</summary>
    private static Dictionary<string, List<MusicInfo>> ReadLibrary()
    {
        using var db = Open();

        // CoverUrl straight into CoverUrl, placeholder and all: Load substitutes it, as it did reading Info.json.
        var songs = db.Query<MusicInfo>("""
                SELECT RelativeLocation, Id, Album, Scan, CoverUrl, LyricsType, LyricsSource, LyricsChecked, Length
                FROM songs
                """)
            .ToDictionary(song => song.RelativeLocation!, StringComparer.Ordinal);
        foreach (var titles in db.Query<NameRow>(
                         "SELECT RelativeLocation, Title AS Name FROM song_titles ORDER BY RelativeLocation, Position")
                     .GroupBy(row => row.RelativeLocation))
            songs[titles.Key].Titles = [.. titles.Select(row => row.Name)];
        foreach (var artists in db.Query<NameRow>(
                         "SELECT RelativeLocation, Artist AS Name FROM song_artists ORDER BY RelativeLocation, Position")
                     .GroupBy(row => row.RelativeLocation))
            songs[artists.Key].Artists = [.. artists.Select(row => row.Name)];

        return songs.Values.GroupBy(song => FolderOf(song.RelativeLocation!), StringComparer.Ordinal)
            .ToDictionary(folder => folder.Key, folder => folder.ToList(), StringComparer.Ordinal);
    }

    /// <summary>
    ///     Writes <paramref name="entries" /> as they now are, after dropping every row in
    ///     <paramref name="folders" />, in one transaction. Each entry is deleted and inserted again rather
    ///     than diffed: the delete cascades to its titles and artists, so the rows always match the entry.
    /// </summary>
    /// <param name="entries">Entries with a <see cref="MusicInfo.RelativeLocation" />, which is the key.</param>
    /// <param name="folders">Folders whose rows are replaced wholesale, so an entry the scan dropped goes too.</param>
    private static void Write(IEnumerable<MusicInfo> entries, IEnumerable<string>? folders = null)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction();

        db.Execute("DELETE FROM songs WHERE Folder = @folder", (folders ?? []).Select(folder => new { folder }),
            transaction);

        foreach (var entry in entries)
        {
            db.Execute("DELETE FROM songs WHERE RelativeLocation = @RelativeLocation", new { entry.RelativeLocation },
                transaction);
            db.Execute("""
                INSERT INTO songs (RelativeLocation, Folder, Id, Album, Scan, CoverUrl, LyricsType, LyricsSource,
                                   LyricsChecked, Length)
                VALUES (@RelativeLocation, @Folder, @Id, @Album, @Scan, @CoverUrl, @LyricsType, @LyricsSource,
                        @LyricsChecked, @Length)
                """, new
            {
                entry.RelativeLocation,
                Folder = FolderOf(entry.RelativeLocation!),
                entry.Id,
                entry.Album,
                entry.Scan,
                CoverUrl = entry.StoredCoverUrl,
                // by name, the way Info.json held them, so reordering an enum relabels nothing
                LyricsType = entry.LyricsType?.ToString(),
                LyricsSource = entry.LyricsSource?.ToString(),
                entry.LyricsChecked,
                entry.Length
            }, transaction);
            db.Execute("INSERT INTO song_titles (RelativeLocation, Position, Title) VALUES (@Location, @Position, @Name)",
                entry.Titles.Select((name, position) => new { Location = entry.RelativeLocation, Position = position, Name = name }),
                transaction);
            db.Execute("INSERT INTO song_artists (RelativeLocation, Position, Artist) VALUES (@Location, @Position, @Name)",
                entry.Artists.Select((name, position) => new { Location = entry.RelativeLocation, Position = position, Name = name }),
                transaction);
        }

        transaction.Commit();
    }

    /// <summary><c>PRAGMA user_version</c> once the tables exist. Each folder's Info.json is imported by the scan.</summary>
    private const int SchemaVersion = 1;

    /// <summary>
    ///     One row per indexed file, keyed by where it is. Titles and artists are ordered lists — the first of
    ///     each leads — so each is its own table with a position.
    /// </summary>
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS songs (
            RelativeLocation TEXT PRIMARY KEY,
            Folder TEXT NOT NULL,
            Id TEXT,
            Album TEXT,
            Scan INTEGER NOT NULL,
            CoverUrl TEXT,
            LyricsType TEXT,
            LyricsSource TEXT,
            LyricsChecked TEXT,
            Length REAL NOT NULL
        ) STRICT;
        CREATE INDEX IF NOT EXISTS songs_by_folder ON songs (Folder);

        CREATE TABLE IF NOT EXISTS song_titles (
            RelativeLocation TEXT NOT NULL REFERENCES songs (RelativeLocation) ON DELETE CASCADE,
            Position INTEGER NOT NULL,
            Title TEXT NOT NULL,
            PRIMARY KEY (RelativeLocation, Position)
        ) STRICT;

        CREATE TABLE IF NOT EXISTS song_artists (
            RelativeLocation TEXT NOT NULL REFERENCES songs (RelativeLocation) ON DELETE CASCADE,
            Position INTEGER NOT NULL,
            Artist TEXT NOT NULL,
            PRIMARY KEY (RelativeLocation, Position)
        ) STRICT;
        """;

    private sealed record NameRow(string RelativeLocation, string Name);

    /// <summary>The folder part of a relative location, in the '/' form the rest of this file uses.</summary>
    private static string FolderOf(string relativeLocation)
    {
        var normalized = relativeLocation.Replace('\\', '/');
        var slash = normalized.LastIndexOf('/');
        return slash < 0 ? string.Empty : normalized[..slash];
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
                     .Select(song => song.RelativeLocation is { } location ? FolderOf(location) : string.Empty)
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
    private static string? PlaylistFor(string folder, string album)
    {
        var directory = Path.Combine(StorageDirectory, folder);
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
        return Songs.Where(song => song.RelativeLocation is { } location && FolderOf(location) == folder);
    }
}
