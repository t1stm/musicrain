using System.Data;
using System.Text.Json;
using Dapper;
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
public class YouTubeCacher
{
    /// <summary><c>PRAGMA user_version</c> once the table exists and <c>YouTube.json</c> is imported.</summary>
    private const int SchemaVersion = 1;

    private const string Columns = "Id, Name, Artist, Album, Duration, ThumbnailUrl, OriginalTitle, OriginalArtist";

    private readonly string _connectionString;
    private readonly string _path;

    /// <param name="logger">Where the cache reports what it did.</param>
    /// <param name="path">
    ///     Where the database lives, <c>YOUTUBE_CACHE_DB</c> by default. That variable named the JSON file
    ///     before, so whatever extension it carries, the database is the <c>.db</c> beside it and the JSON
    ///     is the legacy import.
    /// </param>
    public YouTubeCacher(ILogger logger, string? path = null)
    {
        Logger = logger.ForContext<YouTubeCacher>();
        _path = Path.ChangeExtension(
            path ?? Environment.GetEnvironmentVariable("YOUTUBE_CACHE_DB") ?? "./cache/YouTube.db", ".db");
        _connectionString = Database.At(_path);
    }

    private ILogger Logger { get; }

    public Task InitializeAsync()
    {
        Logger.Information("Opening YouTube cache at: {CachePath}", _path);

        using var db = Database.Open(_connectionString);
        Database.Upgrade(db, SchemaVersion, transaction => Create(db, transaction));
        return Task.CompletedTask;
    }

    /// <summary>Remembers results not seen before. One already cached keeps what it was first cached as.</summary>
    public async Task AddToCacheAsync(IEnumerable<YouTubeResult> results)
    {
        await using var db = Database.Open(_connectionString);
        await using var transaction = await db.BeginTransactionAsync();
        var added = Insert(db, results, transaction);
        await transaction.CommitAsync();

        Logger.Debug("Added {Count} YouTube results to cache", added);
    }

    /// <returns>Up to <paramref name="count" /> distinct cached results, fewer when the cache holds fewer.</returns>
    public async Task<YouTubeResult[]> GetRandomAsync(int count)
    {
        if (count < 1) return [];

        // ponytail: ORDER BY random() visits every row, ~55 ms at half a million. Sample rowids if the
        // discovery endpoint ever shows up in a trace.
        await using var db = Database.Open(_connectionString);
        return (await db.QueryAsync<YouTubeResult>(
            $"SELECT {Columns} FROM results ORDER BY random() LIMIT @count", new { count })).ToArray();
    }

    /// <param name="id">The bare video ID, without <c>yt://</c>.</param>
    /// <returns>The cached result, or <c>null</c> when the ID isn't cached.</returns>
    public async Task<YouTubeResult?> GetFromCacheAsync(string id)
    {
        await using var db = Database.Open(_connectionString);
        return await db.QuerySingleOrDefaultAsync<YouTubeResult>(
            $"SELECT {Columns} FROM results WHERE Id = @id", new { id = "yt://" + id });
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
            Logger.Fatal(e, "Error while importing the YouTube cache from {Path}; starting empty", legacy);
        }
    }
}
