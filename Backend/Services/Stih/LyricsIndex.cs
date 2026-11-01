using System.Data;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Gaida.Sqlite;
using ILogger = Serilog.ILogger;

namespace Stih;

/// <summary>
///     <c>Lyrics.db</c>: one row per track stih has ever looked at, hit or miss.
/// </summary>
/// <remarks>
///     Every answer is one upsert, so there is nothing in memory to lose and nothing to flush on shutdown.
///     <para>
///         There is no cap, deliberately: evicting a row means re-asking LRCLIB for it, which is the one
///         cost this table exists to avoid.
///     </para>
/// </remarks>
public sealed class LyricsIndex
{
    /// <summary><c>PRAGMA user_version</c> once the table exists, <c>Lyrics.json</c> is imported and rows carry a <c>Modified</c>.</summary>
    private const int SchemaVersion = 2;

    private const string Columns = "Id, Type, Source, Volume, Path, Checked";

    /// <summary>What the older versions wrote. Read once, by <see cref="Create" />, and never written.</summary>
    private static readonly JsonSerializerOptions LegacyJson = new()
    {
        Converters = { new JsonStringEnumConverter() },
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _connectionString;
    private readonly ILogger _logger;
    private readonly string _path;

    public LyricsIndex(string dataDirectory, ILogger logger)
    {
        _logger = logger.ForContext<LyricsIndex>();
        _path = Path.Combine(dataDirectory, "Lyrics.db");
        _connectionString = Database.At(_path);

        using var db = Database.Open(_connectionString);
        Database.Upgrade(db, 1,
            transaction => Create(db, transaction, Path.Combine(dataDirectory, "Lyrics.json")));
        // Empty on every existing row, so the first scan after the upgrade reads the whole library once. A
        // table made by this version already has it: Create names it so the import can go through Upsert.
        Database.Upgrade(db, SchemaVersion, transaction =>
        {
            if (!db.Query<string>("SELECT name FROM pragma_table_info('lyrics')", transaction: transaction)
                    .Contains("Modified"))
                db.Execute("ALTER TABLE lyrics ADD COLUMN Modified INTEGER", transaction: transaction);
        });
    }

    public int Count
    {
        get
        {
            using var db = Database.Open(_connectionString);
            return db.ExecuteScalar<int>("SELECT COUNT(*) FROM lyrics");
        }
    }

    public LyricsRow? Get(string id)
    {
        using var db = Database.Open(_connectionString);
        return db.QuerySingleOrDefault<LyricsRow>($"SELECT {Columns} FROM lyrics WHERE Id = @id", new { id });
    }

    /// <summary>
    ///     Whether a miss should be believed. A hit is never re-fetched; a <c>null</c> row older than the
    ///     retry window is offered again, because LRCLIB grows.
    /// </summary>
    public static bool IsFresh(LyricsRow row, TimeSpan retryAfter)
    {
        return row.Type is not null || DateTimeOffset.UtcNow - row.Checked < retryAfter;
    }

    /// <summary>Records one answer. Rows are only ever written after a real answer — never after a timeout.</summary>
    /// <param name="row">The answer.</param>
    /// <param name="modified">
    ///     The file's mtime in ticks, when the scan read it. Anything else records <c>null</c>, which the next
    ///     scan reads as "not seen yet" and records again.
    /// </param>
    public void Record(LyricsRow row, long? modified = null)
    {
        using var db = Database.Open(_connectionString);
        Upsert(db, [row], modified);
    }

    /// <summary>The mtime the scan last recorded for each library <c>.lrc</c>, by its path inside the library.</summary>
    public Dictionary<string, long> Seen()
    {
        using var db = Database.Open(_connectionString);
        var seen = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (path, modified) in db.Query<(string, long)>("""
                     SELECT Path, Modified FROM lyrics
                     WHERE Volume = 'Library' AND Type = 'Synchronized' AND Modified IS NOT NULL
                     """))
            seen[path] = modified;

        return seen;
    }

    /// <summary>Forgets one track, for a row whose file has gone.</summary>
    public void Forget(string id)
    {
        using var db = Database.Open(_connectionString);
        db.Execute("DELETE FROM lyrics WHERE Id = @id", new { id });
    }

    /// <summary>Counts for the admin snapshot, in one pass.</summary>
    public object Snapshot()
    {
        using var db = Database.Open(_connectionString);
        var counts = db.QuerySingle<Counts>("""
            SELECT COUNT(*) AS Rows,
                   COUNT(*) FILTER (WHERE Type = 'Synchronized') AS Synchronized,
                   COUNT(*) FILTER (WHERE Type = 'Unsynchronized') AS Unsynchronized,
                   COUNT(*) FILTER (WHERE Type IS NULL) AS Misses,
                   COUNT(*) FILTER (WHERE Source = 'Lrclib') AS FromLrcLib,
                   COUNT(*) FILTER (WHERE Source = 'Deezer') AS FromDeezer,
                   COUNT(*) FILTER (WHERE Source = 'Generated') AS Generated,
                   COUNT(*) FILTER (WHERE Type IS NOT NULL AND Source IS NULL) AS AlreadyThere,
                   COUNT(*) FILTER (WHERE Volume = 'Library') AS InLibrary,
                   COUNT(*) FILTER (WHERE Volume = 'Own') AS InOwnVolume
            FROM lyrics
            """);

        return new
        {
            rows = counts.Rows,
            synchronized = counts.Synchronized,
            unsynchronized = counts.Unsynchronized,
            misses = counts.Misses,
            fromLrcLib = counts.FromLrcLib,
            fromDeezer = counts.FromDeezer,
            generated = counts.Generated,
            alreadyThere = counts.AlreadyThere,
            inLibrary = counts.InLibrary,
            inOwnVolume = counts.InOwnVolume,
            file = _path
        };
    }

    /// <summary>
    ///     Enums go in by name, so the table reads in the sqlite3 shell the way the JSON did, and so
    ///     reordering an enum cannot silently relabel every row.
    /// </summary>
    private static void Upsert(IDbConnection db, IEnumerable<LyricsRow> rows, long? modified = null,
        IDbTransaction? transaction = null)
    {
        db.Execute($"""
            INSERT OR REPLACE INTO lyrics ({Columns}, Modified)
            VALUES (@Id, @Type, @Source, @Volume, @Path, @Checked, @Modified)
            """,
            rows.Select(row => new
            {
                row.Id,
                Type = row.Type?.ToString(),
                Source = row.Source?.ToString(),
                Volume = row.Volume?.ToString(),
                row.Path,
                row.Checked,
                Modified = modified
            }), transaction);
    }

    /// <summary>
    ///     The table, and the rows of the <c>Lyrics.json</c> an older version wrote. The JSON stays where
    ///     it is, for an older image pointed at this volume.
    /// </summary>
    private void Create(IDbConnection db, IDbTransaction transaction, string legacy)
    {
        db.Execute("""
            CREATE TABLE IF NOT EXISTS lyrics (
                Id TEXT PRIMARY KEY,
                Type TEXT,
                Source TEXT,
                Volume TEXT,
                Path TEXT,
                Checked TEXT NOT NULL,
                Modified INTEGER
            ) STRICT
            """, transaction: transaction);

        if (!File.Exists(legacy)) return;

        List<LyricsRow> rows;
        try
        {
            rows = JsonSerializer.Deserialize<List<LyricsRow>>(File.ReadAllText(legacy), LegacyJson) ?? [];
        }
        catch (Exception e)
        {
            // As before: an unreadable index costs re-asking LRCLIB for the misses. The hits are all
            // still on disk, and /resolve re-finds the library's.
            _logger.Error(e, "Error while importing the lyrics index from {Path}; starting empty", legacy);
            return;
        }

        Upsert(db, rows.Where(row => !string.IsNullOrWhiteSpace(row.Id)), transaction: transaction);
        _logger.Information("Imported {Count} lyrics rows from {Path}", rows.Count, legacy);
    }

    private sealed record Counts(
        long Rows,
        long Synchronized,
        long Unsynchronized,
        long Misses,
        long FromLrcLib,
        long FromDeezer,
        long Generated,
        long AlreadyThere,
        long InLibrary,
        long InOwnVolume);
}
