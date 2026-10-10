using Dapper;
using Gaida.Sqlite;
using Microsoft.Data.Sqlite;

namespace Gaida.Platforms.MusicDatabase.Manager;

/// <summary>
///     <c>library.db</c>, at the root of the library it indexes: one row per indexed file, its titles and artists
///     beside it. Memory is the authority while the pod runs; this is what the next boot starts from.
/// </summary>
internal sealed class LibraryDatabase(LibraryOptions options)
{
    public string Path => System.IO.Path.Combine(options.StorageDirectory, "library.db");

    /// <summary>A connection to <see cref="Path" />, with its tables made if this is the first: one pragma to check.</summary>
    private SqliteConnection Open()
    {
        var db = Database.Open(Database.At(Path));
        Database.Upgrade(db, SchemaVersion, transaction => db.Execute(Schema, transaction: transaction));
        return db;
    }

    /// <summary>Every row, by the folder it sits in.</summary>
    public Dictionary<string, List<MusicInfo>> ReadLibrary()
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
    public void Write(IEnumerable<MusicInfo> entries, IEnumerable<string>? folders = null)
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
                CoverUrl = options.Stored(entry.CoverUrl),
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

    /// <summary>The folder part of a relative location, in the '/' form the library uses throughout.</summary>
    public static string FolderOf(string relativeLocation)
    {
        var normalized = relativeLocation.Replace('\\', '/');
        var slash = normalized.LastIndexOf('/');
        return slash < 0 ? string.Empty : normalized[..slash];
    }
}
