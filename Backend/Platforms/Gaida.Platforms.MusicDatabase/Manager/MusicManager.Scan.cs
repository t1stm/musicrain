using System.Collections.Concurrent;
using System.Text.Json;
using Serilog;

namespace Gaida.Platforms.MusicDatabase.Manager;

/// <summary>Reading the library off disk: the boot scan, a rescan, and the passes that keep old rows current.</summary>
public partial class MusicManager
{
    /// <summary>The file name a cover gets from zero bytes: the SHA-1 of empty input, and no extension.</summary>
    private const string EmptyCoverName = "/da39a3ee5e6b4b0d3255bfef95601890afd80709.";

    public async Task Initialize()
    {
        Logger.Information("Initializing MusicManager");
        if (options.Storage is { } storage)
        {
            Logger.Debug("Ensuring storage directory exists: {Storage}", storage);
            Directory.CreateDirectory(storage);
        }

        if (options.Snimka is null)
            Logger.Warning("SNIMKA_URL is not set: no cover is extracted until it is");

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
            Logger.Debug("Extracting covers from {StorageDirectory}", options.StorageDirectory);
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
        Logger.Debug("Loading music from {StorageDirectory}", options.StorageDirectory);
        var folders = Directory.EnumerateDirectories(options.StorageDirectory, "*", SearchOption.AllDirectories)
            .ToList();
        Logger.Debug("Found {Count} folders in storage", folders.Count);

        var stored = _database.ReadLibrary();
        var parsed = new ConcurrentBag<(string Folder, List<MusicInfo> Entries, bool Changed)>();
        await Parallel.ForEachAsync(folders, async (folder, _) =>
        {
            var relative = Path.GetRelativePath(options.StorageDirectory, folder).Replace('\\', '/');
            var (entries, changed) = await ParseArtistFolder(folder, stored.GetValueOrDefault(relative) ?? []);
            parsed.Add((relative, entries, changed));
        });

        // After the parallel pass rather than inside it, so the scan is one writer and one transaction. Before
        // the cover:// mapping below, so a row is written back in the form it was read in.
        var changed = parsed.Where(folder => folder.Changed).ToList();
        if (changed.Count > 0)
        {
            _database.Write(changed.SelectMany(folder => folder.Entries), changed.Select(folder => folder.Folder));
            Logger.Information("Wrote {Count} folder(s) to {Library}", changed.Count, _database.Path);
        }

        // ponytail: folder order is no longer stable; nothing downstream depends on it (search scores, random shuffles).
        var songs = parsed.SelectMany(f => f.Entries).ToList();

        // An entry whose file is gone -- converted to another format beside it, moved, deleted by hand -- is
        // a search hit that answers every play with a 404. It stays in library.db, ID and all, and comes
        // back at the next boot if the file does.
        var missing = songs.RemoveAll(song => !File.Exists(options.StorageDirectory + "/" + song.RelativeLocation));
        if (missing > 0) Logger.Warning("Left out {Count} entries whose file is gone", missing);

        songs.ForEach(s => s.CoverUrl = LibraryOptions.Served(s.CoverUrl));

        Songs = songs;
    }

    /// <param name="artist">The folder, as an absolute path.</param>
    /// <param name="stored">Its rows in <c>library.db</c>; empty for a folder the database has never seen.</param>
    /// <returns>The folder's entries, and whether they differ from the rows.</returns>
    private async Task<(List<MusicInfo> Entries, bool Changed)> ParseArtistFolder(string artist,
        List<MusicInfo> stored)
    {
        Logger.Information("Loading artist: '{Artist}'", artist);

        var songs = Directory.GetFiles(artist, "*", SearchOption.TopDirectoryOnly)
            .Where(song => AudioFiles.IsAudio(song)).ToList();

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
                                            !AudioFiles.IsAudio(m.RelativeLocation));
        if (stale > 0) Logger.Information("Dropped {Count} non-audio entries for '{Artist}'", stale, artist);

        var legacy = RereadLegacy(existing, artist);
        var behind = CatchUp(existing, artist);
        var reconciled = ReconcileAllLyrics(existing, artist);

        var newFiles = NewFiles(existing, songs).ToList();
        foreach (var file in newFiles)
            existing.Add(ParseFile(file));

        return (existing, imported || stale > 0 || newFiles.Count > 0 || legacy > 0 || behind > 0 || reconciled > 0);
    }

    /// <summary>
    ///     The four-field format took its names from the path, because the tags were read case-sensitively
    ///     and only .mp3 arrives lowercased. NewFiles never revisits an indexed song, so the re-read has to
    ///     happen here or those names stay path-derived forever.
    /// </summary>
    /// <returns>How many entries were re-read.</returns>
    private int RereadLegacy(List<MusicInfo> existing, string artist)
    {
        var legacy = existing.Where(entry => entry.WasLegacy).ToList();
        foreach (var entry in legacy) RereadTags(entry);
        if (legacy.Count > 0)
            Logger.Information("Re-read tags for {Count} entries of '{Artist}'", legacy.Count, artist);

        return legacy.Count;
    }

    /// <summary>
    ///     Passes the scanner had not learned when these entries were indexed. Unlike the legacy re-read this
    ///     must not re-roll the ID, so it goes through Backfill rather than RereadTags.
    /// </summary>
    /// <returns>How many entries were behind.</returns>
    private int CatchUp(List<MusicInfo> existing, string artist)
    {
        var behind = existing.Where(entry => entry.Scan < ScanVersion).ToList();
        foreach (var entry in behind)
        {
            Backfill(entry);
            entry.Scan = ScanVersion;
        }

        if (behind.Count > 0)
            Logger.Information("Backfilled {Count} entries of '{Artist}' to scan {Version}", behind.Count, artist,
                ScanVersion);

        return behind.Count;
    }

    /// <summary>
    ///     The lyrics sidecars, every scan rather than once behind a ScanVersion bump: it is two
    ///     File.Exists per song against a version bump that would re-read every file in the library,
    ///     and it is what lets a .lrc deleted by hand disappear from the index at the next boot.
    /// </summary>
    /// <returns>How many entries changed.</returns>
    private int ReconcileAllLyrics(List<MusicInfo> existing, string artist)
    {
        var reconciled = existing.Count(ReconcileLyrics);
        if (reconciled > 0)
            Logger.Information("Reconciled lyrics on {Count} entries of '{Artist}'", reconciled, artist);

        return reconciled;
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
            Logger.Error(e, "Malformed Info.json for '{Artist}', rebuilding it", folder);
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
            if (_coverExtractor.ExportCover(options.StorageDirectory + "/" + song.RelativeLocation) is not { } cover)
                return;

            song.CoverUrl = LibraryOptions.CoverUrl(cover);
            covered.Add(song);
        });

        if (covered.IsEmpty) return;

        _database.Write(covered);
        Logger.Information("Extracted {Count} cover(s)", covered.Count);
    }

    private void RereadTags(MusicInfo entry)
    {
        var path = options.StorageDirectory + "/" + entry.RelativeLocation;
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
    ///     Every pass, whatever the entry is stamped with: passes 4 and 5 changed what the first three read, so
    ///     they all run again over it.
    /// </remarks>
    private void Backfill(MusicInfo entry)
    {
        var path = options.StorageDirectory + "/" + entry.RelativeLocation;
        if (!File.Exists(path)) return;

        var fresh = MediaInfo.GetInformation(path);

        // Pass 1. An album an admin typed outranks the file, so this only fills a missing one -- or, since
        // pass 5, replaces one the old reader stored garbled, which nobody types.
        if (entry.Album is null || MediaInfo.Garbled(entry.Album)) entry.Album = fresh.Album;

        // Passes 2 and 3, both nothing but AddNames run again.
        var (title, author, folder) = PathNames(path);
        fresh.AddNames(title, author, folder);
        entry.Rederive(fresh);

        // Pass 4. ffprobe guessed a VBR MP3 with no Xing header from its bitrate: 326 s for a 140 s track,
        // which is the length the weak match gates on.
        if (fresh.Duration > TimeSpan.Zero) entry.Duration = fresh.Duration;

        // Also pass 4: a cover named by the SHA-1 of nothing is the zero-byte file an empty TagLib# picture
        // became. Dropped, so the cover pass after the load extracts the real one through ATL.
        if (entry.CoverUrl?.EndsWith(EmptyCoverName, StringComparison.Ordinal) == true) entry.CoverUrl = null;
    }

    private IEnumerable<string> NewFiles(List<MusicInfo> existing, List<string> files)
    {
        return files.Where(location =>
            existing.All(m => m.RelativeLocation != RelativeLocation(location)));
    }

    private string RelativeLocation(string location)
    {
        return Path.GetRelativePath(options.StorageDirectory, location);
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

    private MusicInfo ParseFile(string location)
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
}
