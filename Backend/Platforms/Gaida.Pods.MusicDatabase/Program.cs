using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Gaida.Admin;
using Gaida.Core.Platforms;
using Gaida.Core.Streams;
using Gaida.Core.Utils;
using Gaida.Platforms.MusicDatabase;
using Gaida.Platforms.MusicDatabase.Manager;
using Gaida.Pods.MusicDatabase;
using Microsoft.Data.Sqlite;
using Serilog;

// `dotnet run -- --self-check` runs the pure-logic check below without needing a library or a
// listening host — see RunSelfCheck.
if (args.Contains("--self-check"))
{
    await RunSelfCheck();
    return;
}

Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSerilog();
builder.Services.AddSingleton(Log.Logger);

// An empty value is the same as an unset one, as it was when these travelled as environment variables.
var library = new LibraryOptions(Setting("STORAGE"), Setting("ALBUM_COVERS"), Setting("DOMAIN"));

builder.Services.AddHttpClient(DeezerImport.Client, http => http.Timeout = TimeSpan.FromMinutes(5));

var platform = new MusicDatabase(Log.Logger, library);
platform.Initialize(); // kicks off MusicManager.Initialize() (library scan) in the background
builder.Services.AddSingleton(platform);
builder.Services.AddSingleton(services => new DeezerImport(services.GetRequiredService<IHttpClientFactory>(),
    builder.Configuration["DEEZER_URL"]?.TrimEnd('/'), platform.Library, Log.Logger));

var app = builder.Build();

// Unlike the other pods, this one owns state an operator edits: the library's names and albums.
// No-op without ADMIN_TOKEN. See ADMIN_PLAN.md.
var admin = app.MapAdmin(platform.Library.Summary);

// The rows to edit. A GET, so it goes through Oko's read proxy rather than its audited action one.
admin?.MapGet("/library", IResult (string? q, int? take, MusicDatabase db) =>
    Results.Ok(db.Library.Find(q, take ?? 100).Select(LibraryRow)));

// Repeated `title=` / `artist=` parameters are the whole variant list, in order. A parameter that is
// absent leaves that field alone; `album=` with no value clears it. Read off the query rather than
// bound, because "not sent" and "sent empty" mean different things here and model binding gives both
// as an empty array.
admin?.MapPost("/edit-song", async Task<IResult> (string id, HttpRequest request, MusicDatabase db,
    CancellationToken ct) =>
{
    var titles = Variants(request, "title");
    var artists = Variants(request, "artist");
    var album = request.Query.TryGetValue("album", out var value) ? value.ToString() : null;

    return Answer(await db.Library.EditAsync(id, titles, artists, album, ct), LibraryRow);
});

// Picks up files added to the library by hand since boot. 202 because it only starts the scan — the
// snapshot's `scanning` flag and song count are where the result shows.
admin?.MapPost("/rescan", IResult (MusicDatabase db) => db.Library.TryRescan()
    ? Results.Accepted()
    : Results.Conflict(new ErrorDto("A scan is already running.")));

// Pulls one Deezer track into the library — see DeezerImport.
admin?.MapPost("/import-deezer", async Task<IResult> (string? id, DeezerImport deezer, CancellationToken ct) =>
{
    return Answer(await deezer.ImportAsync(id, ct), LibraryRow);
});

app.MapGet("/classify", IResult (string? query) =>
{
    var (claimed, id, error) = ClassifyAudio(query);
    if (!claimed) return Results.NotFound();
    return error is not null
        ? Results.BadRequest(new ClassifyDto(null, null, error))
        : Results.Ok(new ClassifyDto("id", id, null));
});

app.MapGet("/resolve", async Task<IResult> (string? id, MusicDatabase db, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(id)) return Results.NotFound();
    var bare = StripAudioPrefix(id);
    var result = await db.GetByIdAsync(bare, ct);
    var mapped = result is null ? null : Map(result);
    if (mapped is null) return Results.NotFound();

    // stih writes the .lrc next to the audio on the shared volume, so it needs the path — and this is
    // the route it already has to call to learn the track's name and length. Gaida.API deserializes
    // into its own DTO and ignores fields it does not know, so none of this reaches the public API.
    var entry = db.Library.SearchById(bare);
    return Results.Ok(entry is null
        ? mapped
        : mapped with
        {
            RelativeLocation = entry.RelativeLocation,
            LyricsType = entry.LyricsType?.ToString(),
            LyricsSource = entry.LyricsSource?.ToString()
        });
});

// ── stih's two routes. Ordinary routes rather than /Admin ones: this is service-to-service traffic on
// the internal network, nginx proxies neither, and gating them behind ADMIN_TOKEN would make lyrics
// stop working in a deployment that has chosen not to run the admin panel. ──

// The backfill sweep's work list: tracks with nothing beside them, oldest-checked first. The names are
// the untransliterated tags, because LRCLIB holds tracks under the names they were released with.
app.MapGet("/lyrics/missing", IResult (int? take, int? retryDays, MusicDatabase db) =>
{
    var retryBefore = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-Math.Max(0, retryDays ?? 30));
    return Results.Ok(db.Library.MissingLyrics(take ?? 200, retryBefore).Select(song => new MissingLyricsDto(
        MusicResult.Prefix + song.Id,
        song.Title,
        song.Artist,
        song.Album,
        song.Duration.ToString("c", CultureInfo.InvariantCulture),
        song.RelativeLocation)));
});

// What stih calls after writing a file, and after a lookup that found nothing (`type` omitted). It
// writes no lyrics file: stih has already done that on the volume both pods share.
app.MapPost("/lyrics/stamp", async Task<IResult> (string? id, string? type, string? source, MusicDatabase db,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(id)) return Results.BadRequest(new ErrorDto("A track ID is required."));

    if (!TryParseLyrics<LyricsKind>(type, out var kind))
        return Results.BadRequest(new ErrorDto($"'{type}' is not a lyrics type."));
    if (!TryParseLyrics<LyricsOrigin>(source, out var origin))
        return Results.BadRequest(new ErrorDto($"'{source}' is not a lyrics source."));

    return Answer(await db.Library.StampLyricsAsync(StripAudioPrefix(id), kind, origin, ct),
        entry => new LyricsRowDto(MusicResult.Prefix + entry.Id, entry.LyricsType?.ToString(),
            entry.LyricsSource?.ToString(), entry.LyricsChecked?.ToString("O")));
});

// The routes hand back the sequence itself: ASP.NET serialises an IAsyncEnumerable element by element and
// flushes as it goes. Everything here is already in memory, so what it buys is the client rendering while
// a 200-track roll or a prolific artist is still being written, rather than after the last byte.
app.MapGet("/search", IResult (string? q, MusicDatabase db, CancellationToken ct) =>
    string.IsNullOrWhiteSpace(q)
        ? Results.Ok(Array.Empty<ResultDto>())
        : Results.Ok(Mapped(db.SearchKeywords(q, ct), ct)));

app.MapGet("/random", IResult (int? count, MusicDatabase db, CancellationToken ct) =>
    Results.Ok(Mapped(db.GetRandomResults(Math.Max(0, count ?? 10), ct), ct)));

// This platform has no playlists — nothing here ever claims a query.
app.MapGet("/playlist", IResult (string? _) => Results.NotFound());

app.MapGet("/content", async Task<IResult> (string? id, MusicDatabase db, HttpResponse response,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(id)) return Results.NotFound();

    var result = await db.GetByIdAsync(StripAudioPrefix(id), ct);
    if (result is null) return Results.NotFound();

    var spreader = await result.GetContentDataAsync(ct);
    if (spreader is null) return Results.NotFound();

    // A hybrid WavPack track leaves MusicGetter decoded and re-wrapped as FLAC, so the file's own
    // extension would describe a body it no longer has.
    var extension = result is MusicResult local
        ? WavPack.CorrectionFor(local.Path) is null ? Path.GetExtension(local.Path) : ".flac"
        : string.Empty;
    response.ContentType = AudioFiles.ContentTypeFor(extension);
    response.Headers.Append("Content-Disposition", $"attachment; filename={PlatformId.FileId(id)}{extension}");

    await spreader.DrainToAsync(response.Body, ct);
    return Results.Empty;
});

app.MapGet("/browse", IResult (string? path, MusicDatabase db) =>
{
    // A path nobody has is an empty folder rather than a 404: nothing here reads the disk, so
    // "unknown" and "empty" are the same answer and the client renders both the same way.
    var folder = FolderPath.Normalize(path);
    var (folders, files) = db.Browse(folder);

    var mappedFiles = new List<ResultDto>(files.Count);
    foreach (var file in files)
        if (Map(file) is { } mapped)
            mappedFiles.Add(mapped);

    var mappedFolders = folders.Select(child => new BrowseFolderDto(child.Name,
        folder.Length == 0 ? child.Name : $"{folder}/{child.Name}", child.Songs)).ToArray();

    return Results.Ok(new BrowseDto(folder, mappedFolders, mappedFiles));
});

// A folder's Play All: everything beneath it, already in the explorer's order, streamed straight through.
app.MapGet("/browse/tracks", IResult (string? path, MusicDatabase db, CancellationToken ct) =>
    Results.Ok(Mapped(db.BrowseTracks(path), ct)));

app.MapGet("/artist", async Task<IResult> (string? term, MusicDatabase db, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(term)) return Results.Ok(Array.Empty<ResultDto>());

    // Ordering moved here from Gaida.API so that end can stream the response through untouched: the whole
    // library is already in memory (MusicManager.GetArtistSongs materialises a list anyway), so sorting costs
    // nothing here where it would cost Gaida.API the entire response. The keys mirror
    // DiscoveryResultMapper's preference for the untransliterated fields, so the artist/name/id order API.md
    // documents is still what the client sees.
    var results = new List<ResultDto>();
    await foreach (var result in db.GetArtistSongs(term).WithCancellation(ct))
        if (Map(result) is { } mapped)
            results.Add(mapped);

    var sorted = results
        .OrderBy(result => result.OriginalArtist is { Length: > 0 } artist ? artist : result.Artist ?? "",
            StringComparer.OrdinalIgnoreCase)
        .ThenBy(result => result.OriginalTitle is { Length: > 0 } title ? title : result.Name ?? "",
            StringComparer.OrdinalIgnoreCase)
        .ThenBy(result => result.Id, StringComparer.Ordinal);

    return Results.Ok(sorted);
});

app.MapGet("/album", IResult (string? artist, string? album, MusicDatabase db, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(album))
        return Results.Ok(Array.Empty<ResultDto>());

    // No sort and no buffering, unlike /artist above: an album's order is the playlist file that defines
    // it, so MusicManager already handed these over in the right order and this streams straight through.
    return Results.Ok(Mapped(db.GetAlbumSongs(artist, album), ct));
});

app.MapGet("/variant", IResult (string? name, string? artist, string? duration, MusicDatabase db) =>
{
    if (string.IsNullOrWhiteSpace(name))
        return Results.BadRequest(new ErrorDto("A track name is required."));

    if (!TryParseDuration(duration, out var length, out var error))
        return Results.BadRequest(new ErrorDto(error));

    var found = db.FindLocalVariant(name, artist, length);
    if (found is null) return Results.NoContent();

    var (match, result) = found.Value;
    var mapped = Map(result);
    if (mapped is null) return Results.NoContent();

    return Results.Ok(new VariantDto(MapKind(match.Kind), Math.Round(match.Score, 3),
        (int)Math.Round(match.DurationDelta.TotalSeconds), match.YouTubeTags, match.LibraryTags, mapped));
});

app.Run();
return;

// ── Admin helpers ──────────────────────────────────────────────────────────────────────────────

// An edit, import or stamp as the admin surface and stih read it: the row, a 404 for an unknown song, a 400 else.
static IResult Answer<T>(Change change, Func<MusicInfo, T> row)
{
    if (change.Ok) return Results.Ok(row(change.Entry));
    return change.UnknownSong ? Results.NotFound() : Results.BadRequest(new ErrorDto(change.Error));
}

string? Setting(string key)
{
    return builder.Configuration[key] is { Length: > 0 } value ? value : null;
}

static List<string>? Variants(HttpRequest request, string name)
{
    return request.Query.TryGetValue(name, out var values)
        ? [.. values.Select(value => value ?? string.Empty)]
        : null;
}

// One editable row. Every variant, not just the display one: the whole point of the editor is the
// list ResultDto flattens down to a single name.
static LibraryRowDto LibraryRow(MusicInfo song) => new(song.Id ?? "", [.. song.Titles], [.. song.Artists],
    song.Album, song.RelativeLocation, song.Duration.ToString("c", CultureInfo.InvariantCulture), song.CoverUrl);

// An omitted or empty value is the null case, not a bad request. Anything else must parse.
static bool TryParseLyrics<T>(string? value, out T? parsed) where T : struct, Enum
{
    if (string.IsNullOrWhiteSpace(value))
    {
        parsed = null;
        return true;
    }

    var ok = Enum.TryParse<T>(value, true, out var result);
    parsed = ok ? result : null;
    return ok;
}

static async IAsyncEnumerable<ResultDto> Mapped(IAsyncEnumerable<PlatformResult> source,
    [EnumeratorCancellation] CancellationToken ct)
{
    await foreach (var result in source.WithCancellation(ct))
        if (Map(result) is { } mapped)
            yield return mapped;
}

// ── DTOs — the public shape of this pod. No contentUrl: the platform doesn't know the public host. ──

static ResultDto? Map(PlatformResult result)
{
    if (string.IsNullOrWhiteSpace(result.Id)) return null;
    var duration = result.Duration < TimeSpan.Zero ? TimeSpan.Zero : result.Duration;
    return new ResultDto(result.Id, result.Name, result.Artist, result.Album,
        duration.ToString("c", CultureInfo.InvariantCulture), result.ThumbnailUrl,
        result.OriginalTitle, result.OriginalArtist);
}

// ── /classify, pulled out of the route so RunSelfCheck can exercise it without a host. ──

static (bool Claimed, string? Id, string? Error) ClassifyAudio(string? query)
{
    var trimmed = query?.Trim() ?? string.Empty;
    if (!trimmed.StartsWith(MusicResult.Prefix, StringComparison.OrdinalIgnoreCase))
        return (false, null, null);

    var id = trimmed[MusicResult.Prefix.Length..];
    return string.IsNullOrWhiteSpace(id)
        ? (true, null, "An audio:// query must include an ID.")
        : (true, MusicResult.Prefix + id, null);
}

static string StripAudioPrefix(string id)
{
    return id.StartsWith(MusicResult.Prefix, StringComparison.OrdinalIgnoreCase) ? id[MusicResult.Prefix.Length..] : id;
}

// ── /variant helpers, also pulled out for RunSelfCheck. ──

static bool TryParseDuration(string? duration, out TimeSpan length, [NotNullWhen(false)] out string? error)
{
    length = TimeSpan.Zero;
    error = null;
    if (string.IsNullOrWhiteSpace(duration)) return true;
    if (TimeSpan.TryParse(duration, CultureInfo.InvariantCulture, out length)) return true;

    error = "The duration must look like 00:04:32.";
    return false;
}

static string MapKind(LocalMatchKind kind)
{
    return kind switch
    {
        LocalMatchKind.Same => "same",
        LocalMatchKind.Variant => "variant",
        _ => "weak"
    };
}

// ── The one runnable check: `dotnet run --project Gaida.Pods.MusicDatabase -- --self-check`. ──
// Exercises the pure /classify and /variant logic above without needing a library on disk or a
// listening host. Throws (nonzero exit) the moment any assertion fails.
static async Task RunSelfCheck()
{
    var claimed = ClassifyAudio("audio://abc123");
    Assert(claimed is (true, "audio://abc123", null), "classify: plain id");

    var mixedCase = ClassifyAudio("  AUDIO://Foo  ");
    Assert(mixedCase is (true, "audio://Foo", null), "classify: trims and lowercases only the prefix");

    var empty = ClassifyAudio("audio://");
    Assert(empty is (true, null, "An audio:// query must include an ID."), "classify: missing id is a 400");

    var notMine = ClassifyAudio("yt://dQw4w9WgXcQ");
    Assert(notMine is (false, null, null), "classify: another platform's scheme is unclaimed");

    Assert(ClassifyAudio(null) is (false, null, null), "classify: null query is unclaimed");

    Assert(MapKind(LocalMatchKind.Same) == "same", "variant: Same maps to \"same\"");
    Assert(MapKind(LocalMatchKind.Variant) == "variant", "variant: Variant maps to \"variant\"");
    Assert(MapKind(LocalMatchKind.Weak) == "weak", "variant: Weak maps to \"weak\"");

    Assert(TryParseDuration(null, out var zero, out var noError) && zero == TimeSpan.Zero && noError is null,
        "variant: missing duration parses as zero, not an error");
    Assert(TryParseDuration("00:04:32", out var parsed, out _) &&
           parsed == new TimeSpan(0, 4, 32), "variant: a valid duration round-trips");
    Assert(!TryParseDuration("not-a-duration", out _, out var badError) &&
           badError == "The duration must look like 00:04:32.", "variant: bad duration keeps the exact message");

    await EditCheck(Assert);
    await BackfillCheck(Assert);
    await ImportCheck(Assert);
    await RescanCheck(Assert);

    Console.WriteLine("selftest OK");
    return;

    void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception($"selftest failed: {message}");
    }
}

// The Deezer import, against a throwaway library. Four things would go wrong quietly: the file has
// to land where the rest of the library lives (Deezer/<artist>/<artist> - <title>), the artist
// folder is what keeps "Deezer" out of the entry's own artist list, the entry has to reach
// library.db (otherwise it is gone on the next restart), and a second import of the same track must
// be refused rather than overwrite a file whose entry someone may already have renamed.
//
// The bytes are not real audio, which is the point: ATL reads no tags out of them, so what is
// under test is the filename-and-folder path every import falls back to rather than one particular
// encoder.
static async Task ImportCheck(Action<bool, string> assert)
{
    var root = Path.Combine(Path.GetTempPath(), "gaida-local-import-" + Guid.NewGuid().ToString("n"));
    Directory.CreateDirectory(root);

    var covers = Path.Combine(root, "covers");
    var options = new LibraryOptions(root, covers, "https://music.example.com");

    try
    {
        var database = new MusicDatabase(Serilog.Core.Logger.None, options);
        await database.Library.Initialize();

        var (entry, error, _) = await database.Library.ImportAsync("Daft Punk", "Harder, Better, Faster, Stronger",
            "Discovery", ".mp3", new MemoryStream("not really audio"u8.ToArray()));

        assert(error is null && entry is not null, $"import: the import succeeded ({error})");
        assert(entry!.RelativeLocation == "Deezer/Daft Punk/Daft Punk - Harder, Better, Faster, Stronger.mp3",
            $"import: the library's own layout, one artist folder deep ({entry.RelativeLocation})");
        assert(File.Exists(Path.Combine(root, entry.RelativeLocation!)), "import: the file is on disk");
        assert(!File.Exists(Path.Combine(root, entry.RelativeLocation! + ".part")),
            "import: no half-written .part is left behind");
        assert(entry.Album == "Discovery", "import: the album Deezer supplied fills in for the missing tag");
        assert(entry.Id is { Length: > 0 }, "import: the entry got an ID, so it is addressable as audio://");

        // ParseFile reads the containing folder as an artist variant, so a file sitting directly in
        // Deezer/ would be indexed with "Deezer" as one of its artists -- and answer a search for it.
        assert(!entry.Artists.Contains("Deezer"), "import: the source folder is not one of the artists");
        assert(entry.Artists.Contains("Daft Punk"), "import: the artist folder is the artist");

        assert(Stored(root).Contains("Harder"), "import: the entry reached library.db, so it survives a restart");

        var found = database.Library.Find("Harder", 10);
        assert(found.Count == 1, "import: the entry is in the in-memory library straight away");

        var (duplicate, refused, _) = await database.Library.ImportAsync("Daft Punk", "Harder, Better, Faster, Stronger",
            "Discovery", ".mp3", new MemoryStream("different bytes"u8.ToArray()));
        assert(duplicate is null && refused is not null && refused.Contains("already"),
            "import: a second import of the same name is refused, not silently overwritten");

        var (_, badExtension, _) = await database.Library.ImportAsync("A", "B", null, ".txt", new MemoryStream([1]));
        assert(badExtension is not null, "import: an extension the library cannot play is refused");

        // "/" would make ParseFile read a folder that is not there; " - " would make it read the wrong artist.
        var (slashed, slashError, _) = await database.Library.ImportAsync("AC/DC", "Back - In Black", null, ".mp3",
            new MemoryStream([1, 2, 3]));
        assert(slashError is null && slashed?.RelativeLocation == "Deezer/AC_DC/AC_DC - Back In Black.mp3",
            $"import: a separator in the name is cleaned out of the path ({slashed?.RelativeLocation})");

        // The cover a source supplied, for a file carrying none of its own. Content-addressed, so the
        // name is the hash and importing the same artwork twice writes one file.

        var (art, artError, _) = await database.Library.ImportAsync("Daft Punk", "Aerodynamic", null, ".mp3",
            new MemoryStream([4, 5, 6]), "not really a jpeg"u8.ToArray());
        assert(artError is null && art?.CoverUrl is { Length: > 0 },
            $"import: the supplied cover became this entry's artwork ({artError})");
        assert(art!.CoverUrl!.StartsWith("https://music.example.com/Album_Covers/"),
            $"import: the entry holds the substituted URL, not the placeholder ({art.CoverUrl})");
        assert(Directory.GetFiles(covers).Length == 1, "import: the cover file was written once");
    }
    finally
    {
        try { Directory.Delete(root, true); } catch (IOException) { /* temp dir */ }
    }
}

// The Oko rescan, against a throwaway library that gains a file after boot. What would go wrong quietly:
// the new file not being picked up, or the existing entry's ID being re-rolled by the second read.
static async Task RescanCheck(Action<bool, string> assert)
{
    var root = Path.Combine(Path.GetTempPath(), "gaida-local-rescan-" + Guid.NewGuid().ToString("n"));
    var folder = Path.Combine(root, "Queen");
    Directory.CreateDirectory(folder);
    await File.WriteAllBytesAsync(Path.Combine(folder, "Queen - Bohemian Rhapsody.mp3"), new byte[4096]);

    var options = new LibraryOptions(root, Path.Combine(root, "covers"));

    try
    {
        var database = new MusicDatabase(Serilog.Core.Logger.None, options);
        await database.Library.Initialize();

        var before = database.Library.Find("Queen", 10);
        assert(before.Count == 1, "rescan: the throwaway library loaded one song at boot");

        await File.WriteAllBytesAsync(Path.Combine(folder, "Queen - Somebody to Love.mp3"), new byte[4096]);
        assert(database.Library.TryRescan(), "rescan: a rescan starts when none is running");
        assert(!database.Library.TryRescan(), "rescan: a second one is refused while the first runs");

        for (var waited = 0; Scanning() && waited < 300; waited++) await Task.Delay(100);
        assert(!Scanning(), "rescan: the scan finished and cleared its flag");

        var after = database.Library.Find("Queen", 10);
        assert(after.Count == 2, $"rescan: the file added after boot is in the library ({after.Count})");
        assert(after.Any(song => song.Id == before[0].Id), "rescan: the existing entry kept its ID");
        assert(Stored(root).Contains("Somebody"), "rescan: the new entry reached library.db");
        assert(!File.Exists(Path.Combine(folder, "Info.json")), "rescan: no Info.json is written any more");

        bool Scanning() => System.Text.Json.JsonSerializer.Serialize(database.Library.Summary()).Contains("\"scanning\":true");
    }
    finally
    {
        try { Directory.Delete(root, true); } catch (IOException) { /* temp dir */ }
    }
}

// The album backfill, against a throwaway library whose one entry predates the tag being read. The
// thing that would go wrong quietly is the ID: RereadTags re-rolls it, and re-rolling every ID in
// the library to fill an album would orphan every playlist, cache key and recently-played entry
// that holds one. The media file is a stand-in with no tags, so ATL finds no album and only the
// bookkeeping is under test. A second entry has no file at all: it is left out of the library and
// kept in library.db, where it would come back with its ID if the file did. The library starts as the
// Info.json an older version wrote, so this is the import path too.
static async Task BackfillCheck(Action<bool, string> assert)
{
    var root = Path.Combine(Path.GetTempPath(), "gaida-local-backfill-" + Guid.NewGuid().ToString("n"));
    var folder = Path.Combine(root, "Duran Duran");
    Directory.CreateDirectory(folder);

    var info = Path.Combine(folder, "Info.json");
    await File.WriteAllTextAsync(info, """
        [{"ID":"ducome-un","Titles":["Come Undone"],"Artists":["Duran Duran"],
          "RelativeLocation":"Duran Duran/Duran Duran - Come Undone.mp3","Length":256000},
         {"ID":"duordin-ar","Titles":["Ordinary World"],"Artists":["Duran Duran"],
          "RelativeLocation":"Duran Duran/Duran Duran - Ordinary World.flac","Length":340000}]
        """);
    await File.WriteAllBytesAsync(Path.Combine(folder, "Duran Duran - Come Undone.mp3"), new byte[4096]);

    var options = new LibraryOptions(root);

    try
    {
        var database = new MusicDatabase(Serilog.Core.Logger.None, options);
        await database.Library.Initialize();

        var songs = database.Library.Find("Duran Duran", 10);
        assert(songs.Count == 1, "backfill: the throwaway library loaded the one song on disk");
        assert(songs[0].Id == "ducome-un", "backfill: the ID survives it -- playlists and cache keys hold it");
        assert(songs[0].Scan == MusicManager.ScanVersion, "backfill: the entry is stamped with the pass that read it");

        var saved = Stored(root);
        assert(saved.Contains($"Scan={MusicManager.ScanVersion}"), "backfill: the stamp reached library.db, so it runs once");
        assert(saved.Contains("ducome-un"), "backfill: the saved entry kept its ID");
        assert(saved.Contains("duordin-ar"), "backfill: an entry whose file is gone stays in library.db");
        assert(!(await File.ReadAllTextAsync(info)).Contains("Scan"),
            "backfill: the Info.json it was imported from is left exactly as an older version wrote it");

        // Imported once: what the file says now is not read again.
        await File.WriteAllTextAsync(info, "[]");
        var again = new MusicDatabase(Serilog.Core.Logger.None, options);
        await again.Library.Initialize();
        assert(again.Library.Find("Duran Duran", 10).SingleOrDefault()?.Id == "ducome-un",
            "backfill: a restart reads library.db, not the Info.json it was imported from");
    }
    finally
    {
        try { Directory.Delete(root, true); } catch (IOException) { /* temp dir */ }
    }
}

// The admin edit path, against a throwaway library imported from one Info.json. Covers the two things
// that would go wrong quietly: an edit must not re-roll the ID that playlists and cache keys hold,
// and the saved row must keep the $[DOMAIN] placeholder rather than this host's domain.
static async Task EditCheck(Action<bool, string> assert)
{
    var root = Path.Combine(Path.GetTempPath(), "gaida-local-selfcheck-" + Guid.NewGuid().ToString("n"));
    var folder = Path.Combine(root, "Queen");
    Directory.CreateDirectory(folder);

    var info = Path.Combine(folder, "Info.json");
    await File.WriteAllTextAsync(info, """
        [{"ID":"quyoure-ab","Titles":["You_re My Best Friend"],"Artists":["Queen"],
          "Album":"A Night at the Opera","CoverUrl":"$[DOMAIN]/cover.jpg",
          "RelativeLocation":"Queen/Queen - You_re My Best Friend.mp3","Length":175000}]
        """);
    await File.WriteAllBytesAsync(Path.Combine(folder, "Queen - You_re My Best Friend.mp3"), new byte[4096]);

    var options = new LibraryOptions(root, Domain: "https://music.example.com");

    try
    {
        var database = new MusicDatabase(Serilog.Core.Logger.None, options);
        await database.Library.Initialize();

        var before = database.Library.Find("Queen", 10);
        assert(before.Count == 1, "edit: the throwaway library loaded one song");
        assert(before[0].CoverUrl == "https://music.example.com/Album_Covers/cover.jpg",
            "edit: $[DOMAIN] is substituted on the way in");

        var (edited, error, _) = await database.Library.EditAsync("quyoure-ab",
            ["You're My Best Friend", "You_re My Best Friend"], ["Queen", "Freddie Mercury"], "A Night at the Opera");

        assert(error is null && edited is not null, $"edit: the edit succeeded ({error})");
        assert(edited!.Id == "quyoure-ab", "edit: the ID survives an edit -- playlists and cache keys hold it");
        assert(edited.Title == "You're My Best Friend", "edit: the first title becomes the display name");
        assert(edited.Artists.Count == 2, "edit: every artist variant is kept");

        var saved = Stored(root);
        assert(saved.Contains("You're My Best Friend"), "edit: the new name reached library.db");
        assert(saved.Contains("$[DOMAIN]"), "edit: the cover placeholder is written back, not this host's domain");
        assert(!saved.Contains("music.example.com"), "edit: no absolute domain was baked into the library");

        var (_, blank, _) = await database.Library.EditAsync("quyoure-ab", [" ", ""], null, null);
        assert(blank == "A song needs at least one title.", "edit: a song cannot be left with no title");

        var (missing, notFound, _) = await database.Library.EditAsync("no-such-id", null, null, "X");
        assert(missing is null && notFound == "No song with that ID.", "edit: an unknown ID is refused");

        // Album cleared by an empty value, left alone by a missing one.
        await database.Library.EditAsync("quyoure-ab", null, null, "");
        assert(database.Library.Find("Queen", 10)[0].Album is null, "edit: an empty album clears it");
    }
    finally
    {
        try { Directory.Delete(root, true); } catch (IOException) { /* temp dir */ }
    }
}

// Every row library.db holds, as "column=value" lines: what the self-checks search where they used to
// read Info.json back.
static string Stored(string root)
{
    using var db = new SqliteConnection($"Data Source={Path.Combine(root, "library.db")}");
    db.Open();

    var text = new StringBuilder();
    foreach (var table in (string[])["songs", "song_titles", "song_artists"])
    {
        using var command = db.CreateCommand();
        command.CommandText = $"SELECT * FROM {table}";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            for (var column = 0; column < reader.FieldCount; column++)
                text.Append(reader.GetName(column)).Append('=').Append(reader.GetValue(column)).Append('\n');
    }

    return text.ToString();
}
