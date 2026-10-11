using System.Data;
using System.Text.Json;
using Dapper;
using Gaida.Core.Utils;
using Gaida.Sqlite;
using Serilog;

namespace Gaida.Platforms.YouTube.Cache;

/// <summary>
///     <c>YouTube.db</c>: every search result this pod has ever seen, so a known ID costs no YouTube call
///     and discovery has something to pick from.
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
public class YouTubeCacher(ILogger logger, string path)
{
    /// <summary><c>PRAGMA user_version</c> once the table exists and <c>YouTube.json</c> is imported.</summary>
    private const int SchemaVersion = 1;

    private const string Columns = "Id, Name, Artist, Album, Duration, ThumbnailUrl, OriginalTitle, OriginalArtist";

    private readonly string _path = Path.ChangeExtension(path, ".db");
    private readonly string _connectionString = Database.At(Path.ChangeExtension(path, ".db"));

    private ILogger Logger { get; } = logger.ForContext<YouTubeCacher>();

    public void Initialize()
    {
        Logger.Information("Opening YouTube cache at: {CachePath}", _path);

        using var db = Database.Open(_connectionString);
        Database.Upgrade(db, SchemaVersion, transaction => Create(db, transaction));
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

    /// <summary>How many results are cached. Synchronous because the admin snapshot is; ~25 ms at half a million.</summary>
    public int Count()
    {
        using var db = Database.Open(_connectionString);
        return db.ExecuteScalar<int>("SELECT count(*) FROM results");
    }

    /// <summary>
    ///     One page for Oko's cache browser, newest first. <paramref name="top" /> pins the pages to the rows
    ///     that existed when page 1 was read, so results a search adds meanwhile do not shift them; page 1
    ///     passes <c>null</c> and gets the pin back. The search ignores case in Cyrillic too, which SQLite's
    ///     own <c>LIKE</c> and <c>lower()</c> do not.
    /// </summary>
    /// <remarks>
    ///     ponytail: every search is a full scan, ~0.5 s at half a million rows. An FTS5 shadow table is the
    ///     upgrade if that ever matters; nothing but an operator pressing Enter runs it.
    /// </remarks>
    public async Task<ResultsPage> PageAsync(string? query, int offset, int take, long? top,
        CancellationToken cancellationToken = default)
    {
        var needle = query?.Trim() ?? "";
        offset = Math.Max(0, offset);
        take = Math.Clamp(take, 1, 200);

        await using var db = Database.Open(_connectionString);
        db.CreateFunction("fold", (string? text) => text?.ToLowerInvariant(), isDeterministic: true);

        var pinned = top ?? await db.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT coalesce(max(rowid), 0) FROM results", cancellationToken: cancellationToken));
        var parameters = new { top = pinned, folded = needle.ToLowerInvariant(), raw = needle, offset, take };
        var where = needle.Length == 0
            ? "rowid <= @top"
            : "rowid <= @top AND (instr(fold(coalesce(Name, '') || char(10) || coalesce(Artist, '') || " +
              "char(10) || coalesce(Album, '')), @folded) OR instr(Id, @raw))";

        var total = await db.ExecuteScalarAsync<int>(new CommandDefinition(
            $"SELECT count(*) FROM results WHERE {where}", parameters, cancellationToken: cancellationToken));
        var rows = await db.QueryAsync<YouTubeResult>(new CommandDefinition(
            $"SELECT {Columns} FROM results WHERE {where} ORDER BY rowid DESC LIMIT @take OFFSET @offset",
            parameters, cancellationToken: cancellationToken));

        return new ResultsPage(pinned, total, offset, take, [.. rows]);
    }

    /// <summary>Drops one cached result. A search that lists it again puts it back, as the newest row.</summary>
    /// <param name="id">The video ID, with or without <c>yt://</c>.</param>
    /// <returns>Whether there was a row to drop.</returns>
    public async Task<bool> ForgetAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var db = Database.Open(_connectionString);
        return await db.ExecuteAsync(new CommandDefinition("DELETE FROM results WHERE Id = @id",
            new { id = YouTubeResult.Prefix + PlatformId.StripPrefix(id.Trim()) },
            cancellationToken: cancellationToken)) > 0;
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
public sealed record ResultsPage(long Top, int Total, int Offset, int Take, YouTubeResult[] Rows);
