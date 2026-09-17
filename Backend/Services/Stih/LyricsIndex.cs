using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ILogger = Serilog.ILogger;

namespace Stih;

/// <summary>
///     <c>Lyrics.json</c>: one row per track stih has ever looked at, hit or miss.
/// </summary>
/// <remarks>
///     ponytail: a flat JSON file with whole-snapshot writes and no index. Same shape and roughly the
///     same size as the YouTube search cache, which has been fine for years — one row is about 150 bytes,
///     so the whole library plus every Deezer track anyone has played is a file measured in megabytes. A
///     real store is the upgrade if this ever outgrows the library.
///     <para>
///         There is no cap, deliberately: evicting a row means re-asking LRCLIB for it, which is the one
///         cost this file exists to avoid.
///     </para>
/// </remarks>
public sealed class LyricsIndex : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly ConcurrentDictionary<string, LyricsRow> _rows = new();
    private readonly ILogger _logger;
    private readonly string _path;
    private readonly SemaphoreSlim _writing = new(1, 1);

    private DateTimeOffset _dirtySince = DateTimeOffset.MaxValue;
    private Timer? _timer;

    public LyricsIndex(string dataDirectory, ILogger logger, TimeSpan? debounce = null)
    {
        _logger = logger.ForContext<LyricsIndex>();
        _path = Path.Combine(dataDirectory, "Lyrics.json");
        Directory.CreateDirectory(dataDirectory);
        Load();

        debounce ??= TimeSpan.FromSeconds(5);

        // The sweep produces a row a second and each one is not worth a file write, so the saves are
        // debounced. Shutdown flushes — see DisposeAsync.
        _timer = new Timer(state => _ = FlushIfDirtyAsync(), null, debounce.Value, debounce.Value);
    }

    public int Count => _rows.Count;


    public LyricsRow? Get(string id)
    {
        return _rows.TryGetValue(id, out var row) ? row : null;
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
    public void Record(LyricsRow row)
    {
        _rows[row.Id] = row;
        _dirtySince = DateTimeOffset.UtcNow;
    }

    /// <summary>Forgets one track, for a row whose file has gone.</summary>
    public void Forget(string id)
    {
        if (_rows.TryRemove(id, out _)) _dirtySince = DateTimeOffset.UtcNow;
    }

    /// <summary>Counts for the admin snapshot, in one pass.</summary>
    public object Snapshot()
    {
        var rows = _rows.Values.ToList();
        return new
        {
            rows = rows.Count,
            synchronized = rows.Count(row => row.Type == LyricsKind.Synchronized),
            unsynchronized = rows.Count(row => row.Type == LyricsKind.Unsynchronized),
            misses = rows.Count(row => row.Type is null),
            fromLrcLib = rows.Count(row => row.Source == LyricsOrigin.Lrclib),
            fromDeezer = rows.Count(row => row.Source == LyricsOrigin.Deezer),
            alreadyThere = rows.Count(row => row.Type is not null && row.Source is null),
            inLibrary = rows.Count(row => row.Volume == LyricsVolume.Library),
            inOwnVolume = rows.Count(row => row.Volume == LyricsVolume.Own),
            file = _path
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_timer is not null) await _timer.DisposeAsync();
        _timer = null;

        await FlushIfDirtyAsync();
        _writing.Dispose();
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            _logger.Information("No lyrics index at {Path} yet; starting empty", _path);
            return;
        }

        try
        {
            var rows = JsonSerializer.Deserialize<List<LyricsRow>>(File.ReadAllText(_path), Json) ?? [];
            foreach (var row in rows.Where(row => !string.IsNullOrWhiteSpace(row.Id))) _rows[row.Id] = row;

            _logger.Information("Loaded {Count} lyrics rows from {Path}", _rows.Count, _path);
        }
        catch (Exception e)
        {
            // Like the YouTube cache: an unreadable index costs re-asking LRCLIB for the misses. The hits
            // are all still on disk, and /resolve re-finds the library's.
            _logger.Fatal(e, "Error while loading the lyrics index from {Path}; starting empty", _path);
        }
    }

    private async Task FlushIfDirtyAsync()
    {
        // The timer's own period is the debounce, so "dirty at all" is the only question here.
        if (_dirtySince == DateTimeOffset.MaxValue) return;

        await _writing.WaitAsync();
        try
        {
            if (_dirtySince == DateTimeOffset.MaxValue) return;
            _dirtySince = DateTimeOffset.MaxValue;

            // Snapshot to a temp file and rename, the pattern YouTubeCacher and MusicManager.SaveFolderAsync
            // both use: a crash mid-write leaves the previous index rather than a torn one.
            var temporary = _path + ".tmp";
            await File.WriteAllTextAsync(temporary,
                JsonSerializer.Serialize(_rows.Values.OrderBy(row => row.Id, StringComparer.Ordinal), Json));
            File.Move(temporary, _path, true);

            _logger.Debug("Wrote {Count} lyrics rows to {Path}", _rows.Count, _path);
        }
        catch (Exception e)
        {
            _logger.Fatal(e, "Error while saving the lyrics index to {Path}", _path);
        }
        finally
        {
            _writing.Release();
        }
    }
}