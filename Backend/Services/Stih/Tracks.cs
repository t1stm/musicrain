using System.Collections.Concurrent;
using System.Globalization;
using ILogger = Serilog.ILogger;

namespace Stih;

/// <summary>
///     An ID is all the caller gives, so everything else is asked for: stih turns an ID into a name, an
///     artist and a length by asking the pod that owns the ID — the same <c>/resolve</c> route Gaida.API
///     already uses.
/// </summary>
/// <remarks>
///     That is why the public endpoint takes an ID and nothing else, and why matching quality is the same
///     whatever the caller knew. A prefix with no configured URL, an unreachable pod or a 404 all come
///     back <c>null</c>, which the endpoint answers as 204.
/// </remarks>
public sealed class Tracks(IHttpClientFactory factory, IConfiguration configuration, ILogger logger)
{
    /// <summary>
    ///     Which pod owns which prefix. Adding YouTube lyrics one day is one entry here and nothing else —
    ///     which is the whole reason resolution goes through the owning pod rather than through the caller.
    /// </summary>
    private static readonly (string Prefix, string Setting)[] Owners =
    [
        ("audio://", "Local:Url"),
        ("deezer://", "Deezer:Url")
    ];

    private readonly ConcurrentDictionary<string, (Track? Track, DateTimeOffset At)> _cache = new();
    private readonly ILogger _logger = logger.ForContext<Tracks>();

    /// <summary>A listener re-opening the pane should not re-ask the pod. The sweep never uses this path.</summary>
    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(1);

    public string? LastError { get; private set; }

    /// <summary>The URL of the pod that owns an ID, or <c>null</c> when nothing here does.</summary>
    public string? OwnerOf(string id)
    {
        foreach (var (prefix, setting) in Owners)
            if (id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return configuration[setting]?.TrimEnd('/') is { Length: > 0 } url ? url : null;

        return null;
    }

    /// <summary>The platform segment an own-volume path lives under: <c>deezer</c> for <c>deezer://…</c>.</summary>
    public static string? PlatformOf(string id)
    {
        var separator = id.IndexOf("://", StringComparison.Ordinal);
        return separator > 0 ? id[..separator].ToLowerInvariant() : null;
    }

    /// <summary>What a track is, or <c>null</c> when the owning pod has nothing to say about it.</summary>
    public async Task<Track?> ResolveAsync(string id, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(id, out var cached) && DateTimeOffset.UtcNow - cached.At < CacheFor)
            return cached.Track;

        var url = OwnerOf(id);
        if (url is null)
        {
            // yt:// lands here, and so does a prefix whose pod is not configured in this deployment.
            _logger.Debug("No configured pod owns {ID}", id);
            return null;
        }

        PodResultDto? result;
        try
        {
            var http = factory.CreateClient("pods");
            result = await http.GetFromJsonAsync<PodResultDto>(
                $"{url}/resolve?id={Uri.EscapeDataString(id)}", ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LastError = $"{url}: {e.Message}";
            _logger.Warning(e, "Resolving {ID} against {Url} failed", id, url);
            return null;
        }

        var track = Map(id, result);

        // A failed resolve is deliberately not cached: it is usually a pod still booting, and a listener
        // who waits ten seconds and opens the pane again should get the real answer.
        if (track is not null) _cache[id] = (track, DateTimeOffset.UtcNow);

        return track;
    }

    /// <summary>A sweep row is already everything a resolve would have answered, so it skips the call.</summary>
    public static Track? FromMissing(MissingLyricsDto row)
    {
        return string.IsNullOrWhiteSpace(row.Id) || string.IsNullOrWhiteSpace(row.Title)
            ? null
            : new Track(row.Id, row.Title, row.Artist, row.Album, ParseDuration(row.Duration),
                row.RelativeLocation, null);
    }

    private static Track? Map(string id, PodResultDto? result)
    {
        if (result is null || string.IsNullOrWhiteSpace(result.Name)) return null;

        return new Track(
            id,
            // The untransliterated tag: LRCLIB holds tracks under the names they were released with.
            result.OriginalTitle ?? result.Name,
            result.OriginalArtist ?? result.Artist,
            result.Album,
            ParseDuration(result.Duration),
            result.RelativeLocation,
            Enum.TryParse<LyricsKind>(result.LyricsType, true, out var kind) ? kind : null);
    }

    private static TimeSpan ParseDuration(string? duration)
    {
        return TimeSpan.TryParse(duration, CultureInfo.InvariantCulture, out var parsed) && parsed > TimeSpan.Zero
            ? parsed
            : TimeSpan.Zero;
    }
}
