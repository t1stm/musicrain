using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Gaida.Sqlite;
using ILogger = Serilog.ILogger;

namespace Dom.Store;

/// <summary>
///     <c>dom.db</c>: what <see cref="DomStore" /> reloads from. Every change writes the accounts and playlists it
///     touched, and only those, in one transaction: a half-written account is somebody locked out, and a crash
///     mid-write rolls back to the last whole one.
/// </summary>
/// <param name="path">Where the database lives.</param>
/// <param name="log">Where the database reports what it did.</param>
internal sealed class DomDatabase(string path, ILogger log)
{
    /// <summary>What the older versions wrote. Read once, on the first start with no database.</summary>
    private static readonly JsonSerializerOptions LegacyJson = new();

    private readonly string _connectionString = Database.At(path);
    private readonly ILogger _log = log;

    static DomDatabase()
    {
        SqlMapper.AddTypeHandler(new JsonObjectHandler());
    }

    /// <summary>
    ///     Every account and playlist, bringing the file up to date first — importing the <c>dom.json</c> at
    ///     <paramref name="legacy" /> if this is the first start with no database.
    /// </summary>
    public (IEnumerable<User> Users, IEnumerable<Playlist> Playlists) Load(string legacy)
    {
        using var db = Database.Open(_connectionString);
        // 1: the tables exist and dom.json is imported.
        Database.Upgrade(db, 1, transaction =>
        {
            db.Execute(Schema, transaction: transaction);
            Import(db, transaction, legacy);
        });

        // 2: every account has an Id that a rename does not change. A database made at version 1 has
        // no column for it; one made since got it from Schema, so this only adds the index.
        Database.Upgrade(db, 2, transaction =>
        {
            if (!db.Query<string>("SELECT name FROM pragma_table_info('users')", transaction: transaction)
                    .Contains("Id"))
                db.Execute("ALTER TABLE users ADD COLUMN Id TEXT", transaction: transaction);

            var missing = db.Query<string>("SELECT Key FROM users WHERE Id IS NULL", transaction: transaction);
            db.Execute("UPDATE users SET Id = @id WHERE Key = @key",
                missing.Select(key => new { id = DomStore.NewId(), key }), transaction);

            db.Execute("CREATE UNIQUE INDEX users_by_id ON users (Id)", transaction: transaction);
        });

        var users = db.Query<User>(
                "SELECT Id, Username, Salt, Hash, Iterations, CreatedUtc, Settings, SettingsUpdatedUtc FROM users")
            .ToDictionary(user => user.Key, StringComparer.Ordinal);
        foreach (var row in db.Query<TokenRow>(
                     "SELECT UserKey, Value, IssuedUtc, ExpiresUtc FROM tokens ORDER BY rowid"))
            users[row.UserKey].Tokens.Add(new Token
            { Value = row.Value, IssuedUtc = row.IssuedUtc, ExpiresUtc = row.ExpiresUtc });
        foreach (var friends in db.Query<FriendRow>("SELECT UserKey, Friend FROM friends ORDER BY UserKey, Position")
                     .GroupBy(row => row.UserKey))
            users[friends.Key].Friends = [.. friends.Select(row => row.Friend)];

        var playlists = db.Query<Playlist>(
                "SELECT Id, Owner, Name, Visibility, Revision, CoverFile, CreatedUtc, UpdatedUtc FROM playlists")
            .ToDictionary(playlist => playlist.Id, StringComparer.Ordinal);
        foreach (var team in db.Query<CollaboratorRow>(
                         "SELECT PlaylistId, Collaborator FROM playlist_collaborators ORDER BY PlaylistId, Position")
                     .GroupBy(row => row.PlaylistId))
            playlists[team.Key].Collaborators = [.. team.Select(row => row.Collaborator)];
        foreach (var tracks in db.Query<TrackRow>("""
                         SELECT PlaylistId, Id, Name, Artist, Album, Duration, ThumbnailUrl, AddedBy
                         FROM playlist_tracks ORDER BY PlaylistId, Position
                         """).GroupBy(row => row.PlaylistId))
            playlists[tracks.Key].Tracks = [.. tracks.Select(row => row.ToSnapshot())];

        _log.Information("Loaded {Users} account(s) and {Playlists} playlist(s) from {Path}",
            users.Count, playlists.Count, db.DataSource);

        return (users.Values, playlists.Values);
    }

    /// <summary>
    ///     The accounts in the <c>dom.json</c> an older version wrote, inside the transaction that makes
    ///     the tables. A file that will not parse fails the boot rather than starting empty: these are
    ///     accounts, not a cache, and the database is not stamped until they are all in. The JSON stays
    ///     where it is, for an older image pointed at this volume.
    /// </summary>
    private void Import(IDbConnection db, IDbTransaction transaction, string legacy)
    {
        if (!File.Exists(legacy)) return;

        var state = JsonSerializer.Deserialize<DomState>(File.ReadAllText(legacy), LegacyJson) ?? new DomState();
        foreach (var playlist in state.Playlists)
        {
            // version 1 kept a bool; see Playlist.IsPublic
            if (playlist.IsPublic is { } legacyPublic)
            {
                playlist.Visibility = legacyPublic ? Visibility.Public : Visibility.Private;
                playlist.IsPublic = null;
            }
        }

        Write(db, transaction, state.Users, state.Playlists, [], []);
        _log.Information("Imported {Users} account(s) and {Playlists} playlist(s) from {Path}",
            state.Users.Count, state.Playlists.Count, legacy);
    }

    /// <summary>
    ///     Writes <paramref name="users" /> and <paramref name="playlists" /> as they now are and drops the
    ///     gone ones, in one transaction.
    /// </summary>
    public void Save(IEnumerable<User> users, IEnumerable<Playlist> playlists, IEnumerable<string> goneUsers,
        IEnumerable<string> gonePlaylists)
    {
        using var db = Database.Open(_connectionString);
        using var transaction = db.BeginTransaction();
        Write(db, transaction, users, playlists, goneUsers, gonePlaylists);
        transaction.Commit();
    }

    /// <summary>
    ///     Each account and playlist is deleted and inserted again rather than diffed: the delete cascades
    ///     to its tokens, friends, collaborators and tracks, so the rows always match the object exactly.
    /// </summary>
    private static void Write(IDbConnection db, IDbTransaction transaction, IEnumerable<User> users,
        IEnumerable<Playlist> playlists, IEnumerable<string> goneUsers, IEnumerable<string> gonePlaylists)
    {
        db.Execute("DELETE FROM users WHERE Key = @key", goneUsers.Select(key => new { key }), transaction);
        db.Execute("DELETE FROM playlists WHERE Id = @id", gonePlaylists.Select(id => new { id }), transaction);

        foreach (var user in users)
        {
            db.Execute("DELETE FROM users WHERE Key = @Key", new { user.Key }, transaction);
            db.Execute("""
                INSERT INTO users (Key, Id, Username, Salt, Hash, Iterations, CreatedUtc, Settings, SettingsUpdatedUtc)
                VALUES (@Key, @Id, @Username, @Salt, @Hash, @Iterations, @CreatedUtc, @Settings, @SettingsUpdatedUtc)
                """, user, transaction);
            db.Execute("""
                INSERT INTO tokens (Value, UserKey, IssuedUtc, ExpiresUtc)
                VALUES (@Value, @UserKey, @IssuedUtc, @ExpiresUtc)
                """, user.Tokens.Select(token => new { token.Value, UserKey = user.Key, token.IssuedUtc, token.ExpiresUtc }),
                transaction);
            db.Execute("INSERT INTO friends (UserKey, Position, Friend) VALUES (@UserKey, @Position, @Friend)",
                user.Friends.Select((friend, position) => new { UserKey = user.Key, Position = position, Friend = friend }),
                transaction);
        }

        foreach (var playlist in playlists)
        {
            db.Execute("DELETE FROM playlists WHERE Id = @Id", new { playlist.Id }, transaction);
            db.Execute("""
                INSERT INTO playlists (Id, Owner, Name, Visibility, Revision, CoverFile, CreatedUtc, UpdatedUtc)
                VALUES (@Id, @Owner, @Name, @Visibility, @Revision, @CoverFile, @CreatedUtc, @UpdatedUtc)
                """, new
            {
                playlist.Id,
                playlist.Owner,
                playlist.Name,
                // the name it goes over the wire as, so the table reads the way the API does
                Visibility = playlist.Visibility.ToString().ToLowerInvariant(),
                playlist.Revision,
                playlist.CoverFile,
                playlist.CreatedUtc,
                playlist.UpdatedUtc
            }, transaction);
            db.Execute("""
                INSERT INTO playlist_collaborators (PlaylistId, Position, Collaborator)
                VALUES (@PlaylistId, @Position, @Collaborator)
                """, playlist.Collaborators.Select((collaborator, position) =>
                new { PlaylistId = playlist.Id, Position = position, Collaborator = collaborator }), transaction);
            db.Execute("""
                INSERT INTO playlist_tracks (PlaylistId, Position, Id, Name, Artist, Album, Duration, ThumbnailUrl, AddedBy)
                VALUES (@PlaylistId, @Position, @Id, @Name, @Artist, @Album, @Duration, @ThumbnailUrl, @AddedBy)
                """, playlist.Tracks.Select((track, position) => new
            {
                PlaylistId = playlist.Id,
                Position = position,
                track.Id,
                track.Name,
                track.Artist,
                track.Album,
                track.Duration,
                track.ThumbnailUrl,
                track.AddedBy
            }), transaction);
        }
    }

    /// <summary>
    ///     Names, not keys, in <c>friends</c>, <c>Owner</c> and <c>playlist_collaborators</c>: they hold the
    ///     display name the way the objects do, and a rename rewrites them the way it rewrites the objects.
    ///     The foreign keys are ownership only — what goes when an account or a playlist does.
    /// </summary>
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS users (
            Key TEXT PRIMARY KEY,
            Id TEXT,
            Username TEXT NOT NULL,
            Salt TEXT NOT NULL,
            Hash TEXT NOT NULL,
            Iterations INTEGER NOT NULL,
            CreatedUtc TEXT NOT NULL,
            Settings TEXT,
            SettingsUpdatedUtc TEXT
        ) STRICT;

        CREATE TABLE IF NOT EXISTS tokens (
            Value TEXT PRIMARY KEY,
            UserKey TEXT NOT NULL REFERENCES users (Key) ON DELETE CASCADE,
            IssuedUtc TEXT NOT NULL,
            ExpiresUtc TEXT NOT NULL
        ) STRICT;
        CREATE INDEX IF NOT EXISTS tokens_by_user ON tokens (UserKey);

        CREATE TABLE IF NOT EXISTS friends (
            UserKey TEXT NOT NULL REFERENCES users (Key) ON DELETE CASCADE,
            Position INTEGER NOT NULL,
            Friend TEXT NOT NULL,
            PRIMARY KEY (UserKey, Position)
        ) STRICT;

        CREATE TABLE IF NOT EXISTS playlists (
            Id TEXT PRIMARY KEY,
            Owner TEXT NOT NULL,
            Name TEXT NOT NULL,
            Visibility TEXT NOT NULL,
            Revision INTEGER NOT NULL,
            CoverFile TEXT,
            CreatedUtc TEXT NOT NULL,
            UpdatedUtc TEXT NOT NULL
        ) STRICT;

        CREATE TABLE IF NOT EXISTS playlist_collaborators (
            PlaylistId TEXT NOT NULL REFERENCES playlists (Id) ON DELETE CASCADE,
            Position INTEGER NOT NULL,
            Collaborator TEXT NOT NULL,
            PRIMARY KEY (PlaylistId, Position)
        ) STRICT;

        CREATE TABLE IF NOT EXISTS playlist_tracks (
            PlaylistId TEXT NOT NULL REFERENCES playlists (Id) ON DELETE CASCADE,
            Position INTEGER NOT NULL,
            Id TEXT NOT NULL,
            Name TEXT NOT NULL,
            Artist TEXT NOT NULL,
            Album TEXT,
            Duration TEXT NOT NULL,
            ThumbnailUrl TEXT,
            AddedBy TEXT,
            PRIMARY KEY (PlaylistId, Position)
        ) STRICT;
        """;

    private sealed record TokenRow(string UserKey, string Value, DateTimeOffset IssuedUtc, DateTimeOffset ExpiresUtc);

    private sealed record FriendRow(string UserKey, string Friend);

    private sealed record CollaboratorRow(string PlaylistId, string Collaborator);

    private sealed record TrackRow(
        string PlaylistId,
        string Id,
        string Name,
        string Artist,
        string? Album,
        string Duration,
        string? ThumbnailUrl,
        string? AddedBy)
    {
        public TrackSnapshot ToSnapshot() => new()
        {
            Id = Id,
            Name = Name,
            Artist = Artist,
            Album = Album,
            Duration = Duration,
            ThumbnailUrl = ThumbnailUrl,
            AddedBy = AddedBy
        };
    }

    /// <summary><see cref="User.Settings" /> is opaque here, so it is stored the way it arrived: as JSON.</summary>
    private sealed class JsonObjectHandler : SqlMapper.TypeHandler<JsonObject>
    {
        public override void SetValue(IDbDataParameter parameter, JsonObject? value)
        {
            parameter.Value = value?.ToJsonString() ?? (object)DBNull.Value;
        }

        public override JsonObject? Parse(object value)
        {
            return JsonNode.Parse((string)value)?.AsObject();
        }
    }
}
