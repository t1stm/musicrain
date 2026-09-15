using System.Collections.Concurrent;
using ILogger = Serilog.ILogger;

namespace Stih;

/// <summary>
///     The flow: index, file, resolve, LRCLIB, write, stamp. Everything the endpoint does is here.
/// </summary>
public sealed class Lyrics(
    LyricsIndex index,
    LrcLib lrcLib,
    Tracks tracks,
    IHttpClientFactory factory,
    IConfiguration configuration,
    ILogger logger)
{
    private readonly ILogger _logger = logger.ForContext<Lyrics>();

    /// <summary>
    ///     One lookup per track however many listeners are waiting — the <c>Lazy&lt;Task&gt;</c> pattern
    ///     lifted from <c>Dunav/CacheService.cs</c>. A room of twelve opening the same song costs one request.
    /// </summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<LyricsDto?>>> _inFlight = new();

    private string MusicLibrary => configuration["MUSIC_LIBRARY"] ?? "/music";
    private string DataDirectory => configuration["LYRICS_DATA"] ?? "/lyrics";

    public TimeSpan RetryAfter =>
        TimeSpan.FromDays(double.TryParse(configuration["LYRICS_RETRY_DAYS"], out var days) ? days : 30);

    /// <summary>The words for one track, or <c>null</c> when there are none anywhere.</summary>
    public Task<LyricsDto?> GetAsync(string id, CancellationToken ct = default)
    {
        var lazy = _inFlight.GetOrAdd(id, key => new Lazy<Task<LyricsDto?>>(async () =>
        {
            try
            {
                return await LookupAsync(key, ct);
            }
            finally
            {
                // Out of the map as soon as it is answered: the index and the file are the cache, and a
                // task kept here would serve a listener the answer from before a /register arrived.
                _inFlight.TryRemove(key, out _);
            }
        }));

        return lazy.Value;
    }

    private async Task<LyricsDto?> LookupAsync(string id, CancellationToken ct)
    {
        // 1. An index hit with a file: read it, parse it, answer. Nothing is asked of anybody.
        if (index.Get(id) is { } row)
        {
            if (row.Type is not null && row.Path is not null)
            {
                if (Read(row.Volume, row.Path) is { } stored)
                    return Answer(stored, row.Source, null);

                // The file has gone -- someone tidied the folder. Drop the row and carry on as if new.
                _logger.Information("Lyrics file for {Id} is gone; forgetting the row", id);
                index.Forget(id);
            }
            // 2. A remembered miss, inside the retry window: no call to anything.
            else if (row.Type is null && LyricsIndex.IsFresh(row, RetryAfter))
            {
                return null;
            }
        }

        // 3. What is this track?
        var track = await tracks.ResolveAsync(id, ct);
        if (track is null) return null;

        // A .lrc someone put in the folder by hand, and the path back from a lost index: gaida-local's
        // own Info.json already knows it is there, and the file is the authority either way.
        if (track.KnownType is { } known && track.RelativeLocation is not null &&
            LibraryPath(track.RelativeLocation, known) is { } beside && File.Exists(beside))
        {
            var existing = await File.ReadAllTextAsync(beside, ct);
            index.Record(new LyricsRow(id, known, null, LyricsVolume.Library,
                Relative(track.RelativeLocation, known), DateTimeOffset.UtcNow));

            return Answer(existing, null, Matched(track));
        }

        // 4. LRCLIB.
        var found = await lrcLib.FindAsync(track, ct);
        if (found is null)
        {
            // 5b. A clean "nothing there" is recorded; a timeout or a 429 is not, so it is retried
            // rather than remembered as a miss.
            if (lrcLib.Enabled && lrcLib.LastWasClean) await RecordMissAsync(track, ct);
            return null;
        }

        // 5a. Write, record, stamp, answer.
        var content = found.SyncedLyrics ?? found.PlainLyrics ?? string.Empty;
        var kind = found.SyncedLyrics is not null ? LyricsKind.Synchronized : LyricsKind.Unsynchronized;

        await StoreAsync(track, content, kind, LyricsOrigin.LRCLIB, false, ct);

        return Answer(content, LyricsOrigin.LRCLIB, new MatchedDto(
            found.TrackName ?? track.Title, found.ArtistName, (int)Math.Round(found.Duration ?? 0)));
    }

    /// <summary>
    ///     What a platform pod pushes after a download. A registration always wins over what LRCLIB gave,
    ///     since the platform's own lyrics are matched to the recording by ID rather than by name.
    /// </summary>
    /// <returns>An error for a body with no words or an ID no configured pod owns, or <c>null</c>.</returns>
    public async Task<string?> RegisterAsync(RegisterDto body, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(body.Id)) return "An id is required.";
        if (string.IsNullOrWhiteSpace(body.Lrc) && string.IsNullOrWhiteSpace(body.Text))
            return "A registration needs lyrics in 'lrc', in 'text', or in both.";

        var id = body.Id.Trim();
        if (tracks.OwnerOf(id) is null && Tracks.PlatformOf(id) is null)
            return $"No configured pod owns '{id}'.";

        var content = string.IsNullOrWhiteSpace(body.Lrc) ? body.Text! : body.Lrc!;
        var kind = string.IsNullOrWhiteSpace(body.Lrc) ? LyricsKind.Unsynchronized : LyricsKind.Synchronized;
        var source = Enum.TryParse<LyricsOrigin>(body.Source, true, out var parsed) ? parsed : LyricsOrigin.Deezer;

        // Resolving is what finds a library track's folder. A pod that cannot answer is not fatal here:
        // the words still land in stih's own volume under the ID the caller knows the track by.
        var track = await tracks.ResolveAsync(id, ct)
                    ?? new Track(id, id, null, null, TimeSpan.Zero, null, null);

        await StoreAsync(track, content, kind, source, true, ct);
        return null;
    }

    /// <summary>
    ///     Records a clean "looked, found nothing": a <c>null</c> row here and a <c>LyricsChecked</c> stamp
    ///     on the library's own copy, so the entry says "asked" rather than looking untried.
    /// </summary>
    public async Task RecordMissAsync(Track track, CancellationToken ct = default)
    {
        index.Record(new LyricsRow(track.Id, null, null, null, null, DateTimeOffset.UtcNow));
        await StampAsync(track, null, null, ct);
    }

    /// <summary>Writes one track's words where they belong and records the row. The sweep shares this.</summary>
    /// <param name="overwrite">
    ///     Only a <c>/register</c> call passes <c>true</c>. A <c>.lrc</c> in a library folder may be
    ///     someone's own work, and LRCLIB must not quietly replace it.
    /// </param>
    public async Task StoreAsync(Track track, string content, LyricsKind kind, LyricsOrigin source,
        bool overwrite, CancellationToken ct = default)
    {
        var (absolute, relative, volume) = Destination(track, kind);
        if (absolute is null)
        {
            _logger.Warning("Refusing to write lyrics for {Id}: no safe path for it", track.Id);
            return;
        }

        if (overwrite || !File.Exists(absolute))
            await WriteAsync(absolute, content, ct);
        else
            _logger.Debug("Leaving the existing lyrics file for {Id} alone", track.Id);

        index.Record(new LyricsRow(track.Id, kind, source, volume, relative, DateTimeOffset.UtcNow));
        await StampAsync(track, kind, source, ct);
    }

    /// <summary>
    ///     Tells gaida-local what happened, so its own <c>Info.json</c> records it.
    /// </summary>
    /// <remarks>
    ///     A failed stamp is logged and otherwise ignored: the file is on disk, and gaida-local's next
    ///     scan reconciles the entry from it anyway. Only <c>audio://</c> tracks have an Info.json at all.
    /// </remarks>
    private async Task StampAsync(Track track, LyricsKind? kind, LyricsOrigin? source, CancellationToken ct)
    {
        if (!track.Id.StartsWith("audio://", StringComparison.OrdinalIgnoreCase)) return;
        if (tracks.OwnerOf(track.Id) is not { } url) return;

        var query = $"?id={Uri.EscapeDataString(track.Id)}";
        if (kind is not null) query += $"&type={kind}";
        if (source is not null) query += $"&source={source}";

        try
        {
            var http = factory.CreateClient("pods");
            using var response = await http.PostAsync($"{url}/lyrics/stamp{query}", null, ct);
            if (!response.IsSuccessStatusCode)
                _logger.Debug("gaida-local refused the stamp for {Id}: {Status}", track.Id, response.StatusCode);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.Warning(e, "Stamping {Id} on gaida-local failed", track.Id);
        }
    }

    /// <summary>
    ///     Where one track's words go, as an absolute path, the path to record, and which volume it is in.
    /// </summary>
    /// <remarks>
    ///     Writing into the library is the one genuinely dangerous thing this service does, so it is
    ///     fenced: a relative path from a pod is rejected unless the resolved absolute path is still inside
    ///     <c>MUSIC_LIBRARY</c>. A pod that starts answering with <c>../../etc/passwd</c> gets a log line
    ///     and nothing else.
    /// </remarks>
    public (string? Absolute, string? Relative, LyricsVolume Volume) Destination(Track track, LyricsKind kind)
    {
        if (track.RelativeLocation is { Length: > 0 } location)
        {
            var relative = Relative(location, kind);
            var absolute = LibraryPath(location, kind);
            return absolute is null ? (null, null, LyricsVolume.Library) : (absolute, relative, LyricsVolume.Library);
        }

        // Everything with no library folder to live in goes into stih's own volume, under the same name
        // the pod that owns it uses: /lyrics/deezer/3135556.lrc.
        var platform = Tracks.PlatformOf(track.Id);
        var bare = platform is null ? track.Id : track.Id[(platform.Length + 3)..];
        if (platform is null || bare.Length == 0 || bare.AsSpan().ContainsAny('/', '\\')) return (null, null, LyricsVolume.Own);

        var own = Path.Combine(platform, bare + Extension(kind));
        return (Path.Combine(DataDirectory, own), own, LyricsVolume.Own);
    }

    /// <summary>The words as the endpoint answers them, parsed once.</summary>
    private static LyricsDto Answer(string content, LyricsOrigin? source, MatchedDto? matched)
    {
        var (kind, lines) = LrcParser.Parse(content);
        return new LyricsDto(kind.ToString(), source?.ToString(), lines, LrcParser.TextOf(lines), matched);
    }

    private static MatchedDto Matched(Track track)
    {
        return new MatchedDto(track.Title, track.Artist, (int)Math.Round(track.Duration.TotalSeconds));
    }

    private static string Extension(LyricsKind kind)
    {
        return kind == LyricsKind.Synchronized ? ".lrc" : ".txt";
    }

    private static string Relative(string relativeLocation, LyricsKind kind)
    {
        return Path.ChangeExtension(relativeLocation, Extension(kind));
    }

    /// <summary>The absolute path inside the library, or <c>null</c> when it would land outside it.</summary>
    private string? LibraryPath(string relativeLocation, LyricsKind kind)
    {
        var root = Path.GetFullPath(MusicLibrary);
        var full = Path.GetFullPath(Path.Combine(root, Relative(relativeLocation, kind)));

        var fenced = full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.Ordinal);

        return fenced ? full : null;
    }

    private string? Read(LyricsVolume? volume, string relative)
    {
        var root = volume == LyricsVolume.Own ? Path.GetFullPath(DataDirectory) : Path.GetFullPath(MusicLibrary);
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.Ordinal)) return null;

        return File.Exists(full) ? File.ReadAllText(full) : null;
    }

    /// <summary>Temp file then rename, like every other write in this stack.</summary>
    private static async Task WriteAsync(string absolute, string content, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);

        var temporary = absolute + ".tmp";
        await File.WriteAllTextAsync(temporary, content, ct);
        File.Move(temporary, absolute, true);
    }

    public object Snapshot()
    {
        return new
        {
            library = MusicLibrary,
            data = DataDirectory,
            retryDays = RetryAfter.TotalDays,
            inFlight = _inFlight.Count,
            libraryFiles = CountFiles(MusicLibrary),
            ownFiles = CountFiles(DataDirectory)
        };
    }

    private readonly ConcurrentDictionary<string, (int Count, DateTimeOffset At)> _counts = new();

    /// <summary>
    ///     Lyrics files under one volume, cached for a minute.
    /// </summary>
    /// <remarks>
    ///     Oko polls the snapshot every two seconds while its panel is open, and the library is thousands
    ///     of folders: walking it per poll would make an open admin tab the most expensive thing this
    ///     service does. The number is a rough gauge, so a minute-old one is the same answer.
    /// </remarks>
    private int CountFiles(string root)
    {
        if (_counts.TryGetValue(root, out var cached) && DateTimeOffset.UtcNow - cached.At < TimeSpan.FromMinutes(1))
            return cached.Count;

        var count = -1;
        try
        {
            if (Directory.Exists(root))
                count = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .Count(file => file.EndsWith(".lrc", StringComparison.OrdinalIgnoreCase) ||
                                   file.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
        }
        catch (IOException)
        {
            // Reported as -1 rather than as a failed snapshot: a count is not worth a 500.
        }

        _counts[root] = (count, DateTimeOffset.UtcNow);
        return count;
    }
}
