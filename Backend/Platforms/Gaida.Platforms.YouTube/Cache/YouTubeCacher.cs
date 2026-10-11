using System.Data;
using System.Text.Json;
using Dapper;
using Gaida.Core.Utils;
using Gaida.Sqlite;
using Serilog;

namespace Gaida.Platforms.YouTube.Cache;

/// <summary>
///     <c>YouTube.db</c>: every search result this pod has ever seen, so a known ID costs no YouTube call
///     and discovery has something to pick from — and, in <c>downloads</c>, which of them have a webm in
///     the audio cache, so Oko can list and evict those beside their names.
/// </summary>
/// <remarks>
///     Nothing is held in memory. The JSON file this replaced was rewritten whole on every new search
///     and held whole on the heap, and it had reached half a million entries.
/// </remarks>
/// <param name="logger">Where the cache reports what it did.</param>
/// <param name="path">
///     Where the database lives. Whatever extension it carries, the database is the <c>.db</c> beside it
///     and a <c>.json</c> there is the legacy import — see <see cref="YouTubeOptions.CacheDatabase" />.
/// </param>
/// <param name="audioDirectory">The webm cache <see cref="Getters.GetterLocalCache" /> keeps downloads in.</param>
public class YouTubeCacher(ILogger logger, string path, string audioDirectory)
{
    /// <summary>
    ///     <c>PRAGMA user_version</c> once every table exists and <c>YouTube.json</c> is imported. 1 is a file
    ///     from before the <c>downloads</c> table.
    /// </summary>
    private const int SchemaVersion = 2;

    private const string Columns = "Id, Name, Artist, Album, Duration, ThumbnailUrl, OriginalTitle, OriginalArtist";

    private readonly string _path = Path.ChangeExtension(path, ".db");
    private readonly string _connectionString = Database.At(Path.ChangeExtension(path, ".db"));

    private ILogger Logger { get; } = logger.ForContext<YouTubeCacher>();

    public void Initialize()
    {
        Logger.Information("Opening YouTube cache at: {CachePath}", _path);

        using var db = Database.Open(_connectionString);
        Database.Upgrade(db, 1, transaction => Create(db, transaction));
        Database.Upgrade(db, SchemaVersion, transaction => db.Execute("""
            CREATE TABLE IF NOT EXISTS downloads (
                Id TEXT PRIMARY KEY,
                Bytes INTEGER NOT NULL,
                At REAL NOT NULL
            ) STRICT
            """, transaction: transaction));

        // The backfill: a first start fills the table from the webm files already there.
        SyncDownloads();
    }

    /// <summary>
    ///     Brings <c>downloads</c> in line with the webm cache on disk: a row per <c>&lt;id&gt;.webm</c>, oldest
    ///     file first so the newest download is the newest row, and none for a file that is gone.
    /// </summary>
    /// <remarks>
    ///     The directory is the truth and the table its index, refreshed at startup and whenever Oko loads a
    ///     first page — so a file kept, deleted or copied in by hand shows up without the getter reporting
    ///     it. ~40 ms over 25,000 files. Only <c>.webm</c> counts: that is all GetterLocalCache serves.
    /// </remarks>
    public void SyncDownloads()
    {
        if (!Directory.Exists(audioDirectory)) return;

        var onDisk = new DirectoryInfo(audioDirectory).EnumerateFiles("*.webm")
            .Where(file => file.Extension == ".webm")
            .ToDictionary(file => YouTubeResult.Prefix + Path.GetFileNameWithoutExtension(file.Name));

        using var db = Database.Open(_connectionString);
        var known = db.Query<(string Id, long Bytes)>("SELECT Id, Bytes FROM downloads")
            .ToDictionary(row => row.Id, row => row.Bytes);

        var gone = known.Keys.Where(id => !onDisk.ContainsKey(id)).Select(id => new { id }).ToList();
        // A changed size is a copy that was still landing at the last sync.
        var changed = onDisk.Where(pair => !known.TryGetValue(pair.Key, out var bytes) || bytes != pair.Value.Length)
            .OrderBy(pair => pair.Value.LastWriteTimeUtc)
            .Select(pair => new
            {
                Id = pair.Key,
                Bytes = pair.Value.Length,
                At = new DateTimeOffset(pair.Value.LastWriteTimeUtc).ToUnixTimeMilliseconds() / 1000.0
            })
            .ToList();
        if (gone.Count == 0 && changed.Count == 0) return;

        using var transaction = db.BeginTransaction();
        db.Execute("DELETE FROM downloads WHERE Id = @id", gone, transaction);
        db.Execute("""
            INSERT INTO downloads (Id, Bytes, At) VALUES (@Id, @Bytes, @At)
            ON CONFLICT (Id) DO UPDATE SET Bytes = excluded.Bytes
            """, changed, transaction);
        transaction.Commit();

        Logger.Information("Synced the webm index: {Added} added or resized, {Removed} gone", changed.Count,
            gone.Count);
    }

    /// <summary>Remembers results not seen before. One already cached keeps what it was first cached as.</summary>
    public async Task AddToCacheAsync(IEnumerable<YouTubeResult> results, CancellationToken cancellationToken = default)
    {
        await using var db = Database.Open(_connectionString);
        await using var transaction = await db.BeginTransactionAsync(cancellationToken);
        var added = Insert(db, results, transaction);
        await transaction.CommitAsync(cancellationToken);

        Logger.Debug("Added {Count} YouTube results to cache", added);
    }

    /// <returns>Up to <paramref name="count" /> distinct cached results, fewer when the cache holds fewer.</returns>
    public async Task<YouTubeResult[]> GetRandomAsync(int count, CancellationToken cancellationToken = default)
    {
        if (count < 1) return [];

        // ponytail: ORDER BY random() visits every row, ~55 ms at half a million. Sample rowids if the
        // discovery endpoint ever shows up in a trace.
        await using var db = Database.Open(_connectionString);
        return [.. (await db.QueryAsync<YouTubeResult>(new CommandDefinition(
            $"SELECT {Columns} FROM results ORDER BY random() LIMIT @count", new { count },
            cancellationToken: cancellationToken)))];
    }

    /// <param name="id">The bare video ID, without <c>yt://</c>.</param>
    /// <returns>The cached result, or <c>null</c> when the ID isn't cached.</returns>
    public async Task<YouTubeResult?> GetFromCacheAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var db = Database.Open(_connectionString);
        return await db.QuerySingleOrDefaultAsync<YouTubeResult>(new CommandDefinition(
            $"SELECT {Columns} FROM results WHERE Id = @id", new { id = YouTubeResult.Prefix + id },
            cancellationToken: cancellationToken));
    }

    /// <summary>
    ///     How many results are cached, and how many downloads take how many bytes. Synchronous because the
    ///     admin snapshot is; a few milliseconds at half a million rows.
    /// </summary>
    public (int Results, int Downloads, long DownloadBytes) Count()
    {
        using var db = Database.Open(_connectionString);
        return db.QuerySingle<(int, int, long)>("""
            SELECT (SELECT count(*) FROM results), count(*), coalesce(sum(Bytes), 0) FROM downloads
            """);
    }

    /// <summary>
    ///     One page for Oko's cache browser: every cached result with its download beside it, then the
    ///     downloads no result describes — or, with <paramref name="downloaded" />, only the downloads, newest
    ///     first. <paramref name="top" /> pins the pages to the rows that existed when page 1 was read, so
    ///     results a search adds meanwhile do not shift them; page 1 passes <c>null</c>, gets the pin back,
    ///     and re-syncs the webm index first. The search ignores case in Cyrillic too, which SQLite's own
    ///     <c>LIKE</c> and <c>lower()</c> do not.
    /// </summary>
    /// <remarks>
    ///     Two runs read one after the other rather than one UNION: sorting the union costs ~600 ms a page,
    ///     where each run walks its own rowid. ponytail: every search is a full scan, ~0.5 s at half a
    ///     million rows. An FTS5 shadow table is the upgrade if that ever matters; nothing but an operator
    ///     pressing Enter runs it.
    /// </remarks>
    public async Task<TracksPage> PageAsync(string? query, int offset, int take, long? top, bool downloaded = false,
        CancellationToken cancellationToken = default)
    {
        var needle = query?.Trim() ?? "";
        offset = Math.Max(0, offset);
        take = Math.Clamp(take, 1, 200);
        if (top is null) SyncDownloads();

        await using var db = Database.Open(_connectionString);
        db.CreateFunction("fold", (string? text) => text?.ToLowerInvariant(), isDeterministic: true);

        if (downloaded)
        {
            var newest = top ?? await Scalar<long>(db, "SELECT coalesce(max(rowid), 0) FROM downloads", null,
                cancellationToken);
            var parameters = Parameters(newest, needle, offset, take);
            var where = "d.rowid <= @top" + Match("d.Id", needle);
            const string from = "downloads d LEFT JOIN results r ON r.Id = d.Id";

            return new TracksPage(newest,
                await Scalar<int>(db, $"SELECT count(*) FROM {from} WHERE {where}", parameters, cancellationToken),
                offset, take,
                await Rows(db, $"SELECT {DownloadColumns} FROM {from} WHERE {where} ORDER BY d.rowid DESC " +
                               "LIMIT @take OFFSET @offset", parameters, cancellationToken));
        }

        var pinned = top ?? await Scalar<long>(db, "SELECT coalesce(max(rowid), 0) FROM results", null,
            cancellationToken);
        var described = Parameters(pinned, needle, offset, take);
        var resultsWhere = "r.rowid <= @top" + Match("r.Id", needle);
        // Nothing to fold for a download with no result: it has an ID and nothing else. This run is not
        // pinned and needs no pin: only a page-1 sync adds to it, and that drops the pin anyway.
        var undescribedWhere = "NOT EXISTS (SELECT 1 FROM results r WHERE r.Id = d.Id)" +
                               (needle.Length == 0 ? "" : " AND instr(d.Id, @raw)");

        var known = await Scalar<int>(db, $"SELECT count(*) FROM results r WHERE {resultsWhere}", described,
            cancellationToken);
        var undescribed = await Scalar<int>(db, $"SELECT count(*) FROM downloads d WHERE {undescribedWhere}",
            described, cancellationToken);

        var rows = new List<CachedTrack>(take);
        if (offset < known)
            rows.AddRange(await Rows(db,
                $"SELECT {ResultColumns} FROM results r LEFT JOIN downloads d ON d.Id = r.Id WHERE {resultsWhere} " +
                "ORDER BY r.rowid DESC LIMIT @take OFFSET @offset", described, cancellationToken));
        if (rows.Count < take)
            rows.AddRange(await Rows(db,
                $"SELECT {DownloadColumns} FROM downloads d LEFT JOIN results r ON r.Id = d.Id " +
                $"WHERE {undescribedWhere} ORDER BY d.rowid DESC LIMIT @take OFFSET @offset",
                Parameters(pinned, needle, Math.Max(0, offset - known), take - rows.Count), cancellationToken));

        return new TracksPage(pinned, known + undescribed, offset, take, [.. rows]);
    }

    /// <summary>Whether a webm of this video is in the audio cache, as of the last sync.</summary>
    public async Task<bool> IsDownloadedAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var db = Database.Open(_connectionString);
        return await Scalar<int>(db, "SELECT count(*) FROM downloads WHERE Id = @id", new { id = FullId(id) },
            cancellationToken) > 0;
    }

    /// <summary>Drops one cached result. A search that lists it again puts it back, as the newest row.</summary>
    /// <param name="id">The video ID, with or without <c>yt://</c>.</param>
    /// <returns>Whether there was a row to drop.</returns>
    public async Task<bool> ForgetAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var db = Database.Open(_connectionString);
        return await db.ExecuteAsync(new CommandDefinition("DELETE FROM results WHERE Id = @id",
            new { id = FullId(id) }, cancellationToken: cancellationToken)) > 0;
    }

    /// <summary>
    ///     Deletes one video's webm and its row. The next play downloads it again. A reader streaming the file
    ///     right now keeps its handle and finishes.
    /// </summary>
    /// <returns>Whether there was a download to delete. <c>false</c> for anything that is not a video ID.</returns>
    public async Task<bool> EvictAsync(string id, CancellationToken cancellationToken = default)
    {
        // The ID becomes a file name, so nothing but YouTube's own alphabet gets that far: no "../".
        var pure = PlatformId.StripPrefix(id.Trim());
        if (pure.Length == 0 || !pure.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return false;

        var file = Path.Combine(audioDirectory, pure + ".webm");
        var existed = File.Exists(file);
        File.Delete(file);

        await using var db = Database.Open(_connectionString);
        var removed = await db.ExecuteAsync(new CommandDefinition("DELETE FROM downloads WHERE Id = @id",
            new { id = YouTubeResult.Prefix + pure }, cancellationToken: cancellationToken)) > 0;

        if (existed) Logger.Information("Evicted {ID} from the webm cache", pure);
        return existed || removed;
    }

    private const string ResultColumns = """
        r.Id, r.Name, r.Artist, r.Album, r.Duration, r.ThumbnailUrl, r.OriginalTitle, r.OriginalArtist,
        iif(d.Id IS NULL, NULL, 'webm') AS Format, d.Bytes, d.At
        """;

    private const string DownloadColumns = """
        d.Id, r.Name, r.Artist, r.Album, r.Duration, r.ThumbnailUrl, r.OriginalTitle, r.OriginalArtist,
        'webm' AS Format, d.Bytes, d.At
        """;

    private static string FullId(string id)
    {
        return YouTubeResult.Prefix + PlatformId.StripPrefix(id.Trim());
    }

    private static string Match(string id, string needle)
    {
        return needle.Length == 0
            ? ""
            : " AND (instr(fold(coalesce(r.Name, '') || char(10) || coalesce(r.Artist, '') || char(10) || " +
              $"coalesce(r.Album, '')), @folded) OR instr({id}, @raw))";
    }

    private static object Parameters(long top, string needle, int offset, int take)
    {
        return new { top, folded = needle.ToLowerInvariant(), raw = needle, offset, take };
    }

    private static Task<T> Scalar<T>(IDbConnection db, string sql, object? parameters, CancellationToken token)
    {
        return db.ExecuteScalarAsync<T>(new CommandDefinition(sql, parameters, cancellationToken: token))!;
    }

    private static async Task<CachedTrack[]> Rows(IDbConnection db, string sql, object parameters,
        CancellationToken token)
    {
        return [.. await db.QueryAsync<CachedTrack>(new CommandDefinition(sql, parameters, cancellationToken: token))];
    }

    /// <summary><c>Id</c> is a field, and Dapper binds parameters from properties only.</summary>
    private static int Insert(IDbConnection db, IEnumerable<YouTubeResult> results, IDbTransaction transaction)
    {
        return db.Execute($"""
            INSERT OR IGNORE INTO results ({Columns})
            VALUES (@Id, @Name, @Artist, @Album, @Duration, @ThumbnailUrl, @OriginalTitle, @OriginalArtist)
            """, results.Select(result => new
        {
            result.Id,
            result.Name,
            result.Artist,
            result.Album,
            result.Duration,
            result.ThumbnailUrl,
            result.OriginalTitle,
            result.OriginalArtist
        }), transaction);
    }

    /// <summary>
    ///     The table, and the entries of the <c>YouTube.json</c> an older version wrote. The JSON stays
    ///     where it is, for an older image pointed at this volume.
    /// </summary>
    private void Create(IDbConnection db, IDbTransaction transaction)
    {
        db.Execute("""
            CREATE TABLE IF NOT EXISTS results (
                Id TEXT PRIMARY KEY,
                Name TEXT,
                Artist TEXT,
                Album TEXT,
                Duration TEXT NOT NULL,
                ThumbnailUrl TEXT,
                OriginalTitle TEXT,
                OriginalArtist TEXT
            ) STRICT
            """, transaction: transaction);

        var legacy = Path.ChangeExtension(_path, ".json");
        if (!File.Exists(legacy)) return;

        try
        {
            using var file = File.OpenRead(legacy);
            var results = JsonSerializer.Deserialize<YouTubeResult[]>(file, JsonSerializerOptions.Web) ?? [];
            var added = Insert(db, results, transaction);
            Logger.Information("Imported {Count} of {Total} YouTube results from {Path}", added, results.Length,
                legacy);
        }
        catch (JsonException e)
        {
            // As before: an unreadable cache costs re-searching YouTube, nothing else.
            Logger.Error(e, "Error while importing the YouTube cache from {Path}; starting empty", legacy);
        }
    }
}

/// <summary>One page of <see cref="YouTubeCacher.PageAsync" />, with the pin and the clamped bounds it was read with.</summary>
public sealed record TracksPage(long Top, int Total, int Offset, int Take, CachedTrack[] Rows);

/// <summary>
///     One row of Oko's YouTube table: a cached result, a download, or both. A download no result describes
///     has only its ID; one that is not downloaded has no <c>Format</c>, <c>Bytes</c> or <c>At</c> (Unix
///     seconds) — the same names the Deezer pod's rows use.
/// </summary>
/// <remarks>
///     Properties rather than a positional record: Dapper matches a constructor by the reader's column
///     types, and a column that is NULL on every row reads as a blob.
/// </remarks>
public sealed record CachedTrack
{
    public string Id { get; init; } = "";
    public string? Name { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public string? Duration { get; init; }
    public string? ThumbnailUrl { get; init; }
    public string? OriginalTitle { get; init; }
    public string? OriginalArtist { get; init; }
    public string? Format { get; init; }
    public long? Bytes { get; init; }
    public double? At { get; init; }
}
