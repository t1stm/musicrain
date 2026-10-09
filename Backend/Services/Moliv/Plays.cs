using Dapper;
using Gaida.Sqlite;

namespace Moliv;

/// <summary>
///     <c>plays.db</c>: one row per play, on every device, kept however short. A two-second skip is the
///     clearest signal a recommender will get; only the history views hide it (<see cref="Shown" />).
/// </summary>
/// <remarks>
///     <c>StartedUtc</c> is always written at <c>+00:00</c>, so its text order is its time order — which is
///     what the indexes, the cursor and <c>MAX</c> all lean on.
/// </remarks>
public sealed class Plays
{
    /// <summary>What a history view counts as a play: half a minute heard, or a short track heard to its end.</summary>
    private const string Shown = "(PlayedMs >= 30000 OR EndReason = 'finished')";

    private const string Schema = """
        CREATE TABLE plays (
            Id TEXT PRIMARY KEY,
            UserId TEXT,
            DeviceId TEXT NOT NULL,
            TrackId TEXT NOT NULL,
            StartedUtc TEXT NOT NULL,
            UtcOffsetMinutes INTEGER NOT NULL,
            DurationMs INTEGER NOT NULL,
            PlayedMs INTEGER NOT NULL,
            StartReason TEXT NOT NULL,
            EndReason TEXT,
            SourceKind TEXT NOT NULL,
            SourceId TEXT,
            SessionId TEXT NOT NULL,
            SessionPosition INTEGER NOT NULL,
            Platform TEXT NOT NULL,
            DeviceKind TEXT NOT NULL,
            Shuffled INTEGER NOT NULL,
            UpdatedUtc TEXT NOT NULL
        ) STRICT;

        CREATE INDEX plays_by_user ON plays (UserId, StartedUtc, Id);
        CREATE INDEX plays_by_device ON plays (DeviceId, StartedUtc, Id) WHERE UserId IS NULL;
        CREATE INDEX plays_anonymous_by_age ON plays (StartedUtc) WHERE UserId IS NULL;
        """;

    private readonly int _anonymousLimit;
    private readonly string _connectionString;

    /// <param name="path">Where <c>plays.db</c> lives.</param>
    /// <param name="anonymousLimit">How many anonymous plays one device keeps. The self-check turns it down.</param>
    public Plays(string path, int anonymousLimit = 1000)
    {
        _anonymousLimit = anonymousLimit;
        _connectionString = Database.At(path);

        using var db = Database.Open(_connectionString);
        Database.Upgrade(db, 1, transaction => db.Execute(Schema, transaction: transaction));
    }

    /// <summary>How long an anonymous play waits to be claimed before it goes.</summary>
    public static TimeSpan AnonymousLifetime => TimeSpan.FromDays(90);

    /// <summary>
    ///     Creates the play, or merges into it so a retry or a late outbox flush can never make it worse:
    ///     the larger <c>PlayedMs</c>, the first <c>EndReason</c>, the account once one is known, and
    ///     everything else as the first write had it.
    /// </summary>
    /// <returns><c>false</c> when the play exists under another device, which is not this caller's to touch.</returns>
    public bool Upsert(PlayRow row)
    {
        using var db = Database.Open(_connectionString);
        using var transaction = db.BeginTransaction();

        // The WHERE belongs to the DO UPDATE: on another device's row it updates nothing, and nothing
        // changed is how the refusal is told apart from a write.
        var changed = db.Execute("""
            INSERT INTO plays (Id, UserId, DeviceId, TrackId, StartedUtc, UtcOffsetMinutes, DurationMs, PlayedMs,
                               StartReason, EndReason, SourceKind, SourceId, SessionId, SessionPosition, Platform,
                               DeviceKind, Shuffled, UpdatedUtc)
            VALUES (@Id, @UserId, @DeviceId, @TrackId, @StartedUtc, @UtcOffsetMinutes, @DurationMs, @PlayedMs,
                    @StartReason, @EndReason, @SourceKind, @SourceId, @SessionId, @SessionPosition, @Platform,
                    @DeviceKind, @Shuffled, @UpdatedUtc)
            ON CONFLICT (Id) DO UPDATE SET
                PlayedMs = MAX(PlayedMs, excluded.PlayedMs),
                EndReason = COALESCE(EndReason, excluded.EndReason),
                UserId = COALESCE(UserId, excluded.UserId),
                UpdatedUtc = excluded.UpdatedUtc
            WHERE DeviceId = excluded.DeviceId
            """, row, transaction);

        // Nothing proves an anonymous device ID, so this is the one write in the stack anybody can make.
        // The per-device trim bounds one device; the expiry bounds them all, since a new UUID per request
        // is a new device.
        if (row.UserId is null)
            db.Execute("""
                DELETE FROM plays WHERE UserId IS NULL AND DeviceId = @DeviceId AND Id NOT IN (
                    SELECT Id FROM plays WHERE UserId IS NULL AND DeviceId = @DeviceId
                    ORDER BY StartedUtc DESC, Id DESC LIMIT @limit);
                DELETE FROM plays WHERE UserId IS NULL AND StartedUtc < @cutoff;
                """, new { row.DeviceId, limit = _anonymousLimit, cutoff = row.UpdatedUtc - AnonymousLifetime },
                transaction);

        transaction.Commit();
        return changed > 0;
    }

    /// <summary>The caller's plays, newest first, from just before <paramref name="before" />.</summary>
    public List<PlayDto> List(Caller caller, (DateTimeOffset startedUtc, string id)? before, int limit)
    {
        using var db = Database.Open(_connectionString);
        var page = before is null ? "" : "AND (StartedUtc, Id) < (@startedUtc, @id)";

        return db.Query<PlayDto>($"""
            SELECT Id, TrackId, StartedUtc, PlayedMs, DurationMs, EndReason FROM plays
            WHERE {Owner(caller)} AND {Shown} {page}
            ORDER BY StartedUtc DESC, Id DESC LIMIT @limit
            """, new
        {
            caller.UserId, caller.DeviceId, limit,
            startedUtc = before?.startedUtc, id = before?.id
        }).AsList();
    }

    /// <summary>Distinct tracks, most recently played first.</summary>
    /// <remarks>ponytail: groups every play the caller has; an index on (UserId, TrackId) when that shows in a trace.</remarks>
    public List<RecentDto> Recent(Caller caller, int limit)
    {
        using var db = Database.Open(_connectionString);
        return db.Query<RecentDto>($"""
            SELECT TrackId, MAX(StartedUtc) AS StartedUtc FROM plays
            WHERE {Owner(caller)} AND {Shown}
            GROUP BY TrackId ORDER BY StartedUtc DESC LIMIT @limit
            """, new { caller.UserId, caller.DeviceId, limit }).AsList();
    }

    /// <summary>Moves the device's anonymous plays onto the account that just signed in on it.</summary>
    public int Claim(string userId, string deviceId)
    {
        using var db = Database.Open(_connectionString);
        return db.Execute("UPDATE plays SET UserId = @userId WHERE DeviceId = @deviceId AND UserId IS NULL",
            new { userId, deviceId });
    }

    /// <summary>Signed in, every play on the account, from every device; signed out, this device's anonymous ones.</summary>
    public int Clear(Caller caller)
    {
        using var db = Database.Open(_connectionString);
        return db.Execute($"DELETE FROM plays WHERE {Owner(caller)}", new { caller.UserId, caller.DeviceId });
    }

    /// <summary>The account is gone; so is its history.</summary>
    public int Forget(string userId)
    {
        using var db = Database.Open(_connectionString);
        return db.Execute("DELETE FROM plays WHERE UserId = @userId", new { userId });
    }

    /// <remarks>ponytail: counts the whole table per call, which is fine at Oko's polling rate until it is not.</remarks>
    public object Snapshot()
    {
        using var db = Database.Open(_connectionString);
        var row = db.QuerySingle<SnapshotRow>("""
            SELECT COUNT(*) AS Plays, COUNT(DISTINCT UserId) AS Accounts,
                   (SELECT COUNT(DISTINCT DeviceId) FROM plays WHERE UserId IS NULL) AS AnonymousDevices,
                   MAX(UpdatedUtc) AS LastWriteUtc
            FROM plays
            """);
        return new { row.Plays, row.Accounts, row.AnonymousDevices, row.LastWriteUtc };
    }

    private static string Owner(Caller caller) =>
        caller.UserId is null ? "UserId IS NULL AND DeviceId = @DeviceId" : "UserId = @UserId";

    /// <summary>
    ///     Properties, not a positional record: on an empty table <c>MAX</c> is a NULL with no type, which
    ///     Dapper reads as a blob and cannot match to a constructor's <c>string</c>.
    /// </summary>
    private sealed class SnapshotRow
    {
        public long Plays { get; init; }
        public long Accounts { get; init; }
        public long AnonymousDevices { get; init; }
        public string? LastWriteUtc { get; init; }
    }
}

/// <summary>One row of <c>plays</c>, as written. See HISTORY_PLAN.md §A4 for what each column means.</summary>
public sealed record PlayRow(
    string Id,
    string? UserId,
    string DeviceId,
    string TrackId,
    DateTimeOffset StartedUtc,
    int UtcOffsetMinutes,
    long DurationMs,
    long PlayedMs,
    string StartReason,
    string? EndReason,
    string SourceKind,
    string? SourceId,
    string SessionId,
    int SessionPosition,
    string Platform,
    string DeviceKind,
    bool Shuffled,
    DateTimeOffset UpdatedUtc);

/// <summary>Who is asking: always a device, and an account when a token came with it.</summary>
public sealed record Caller(string DeviceId, string? UserId);
