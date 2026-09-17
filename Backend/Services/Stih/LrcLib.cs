using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using ILogger = Serilog.ILogger;

namespace Stih;

/// <summary>
///     The only client of lrclib.net in this stack.
/// </summary>
/// <remarks>
///     One rate limiter, one <c>User-Agent</c>, one place where the rules live — which is also why stih
///     is single-instance. LRCLIB asks for two things: identify the client, and send requests
///     sequentially. Both are honoured here and nowhere else.
///     <para>
///         Every failure that is not a clean "not found" returns <c>null</c> and writes no row, so a bad
///         afternoon costs a retry rather than a permanent "no lyrics" for a track that has them.
///     </para>
/// </remarks>
public sealed class LrcLib
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    /// <summary>One request in flight, ever. This is the promise a second replica would break.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly IHttpClientFactory _factory;
    private readonly ILogger _logger;
    private readonly string _baseUrl;
    private readonly string _userAgent;

    public LrcLib(IHttpClientFactory factory, IConfiguration configuration, ILogger logger)
    {
        _factory = factory;
        _logger = logger.ForContext<LrcLib>();
        _baseUrl = (configuration["LRCLIB_URL"] ?? string.Empty).TrimEnd('/');
        _userAgent = configuration["LRCLIB_USER_AGENT"] ?? "musicrain/1.0 (+https://github.com/t1stm/musicrain)";

        if (!Enabled)
            _logger.Information("LRCLIB_URL is empty: stih serves the files it has and fetches nothing");
    }

    /// <summary>An empty <c>LRCLIB_URL</c> disables every outbound lookup, logged once at boot.</summary>
    public bool Enabled => _baseUrl.Length > 0;

    /// <summary>Set by a 429's <c>Retry-After</c>. The sweep checks it; the on-demand path just fails.</summary>
    public DateTimeOffset? PausedUntil { get; private set; }

    private int Requests { get; set; }
    private int Hits { get; set; }
    private int Misses { get; set; }
    private int RateLimited { get; set; }
    private string? LastError { get; set; }

    /// <summary>
    ///     Everything LRCLIB has for one track: the exact hit, then the fuzzy one, at most two requests.
    /// </summary>
    /// <returns>
    ///     The candidate to use, or <c>null</c> for both "nothing there" and "could not ask" — which
    ///     <see cref="LastWasClean" /> tells apart: <c>false</c> means write no row.
    /// </returns>
    public async Task<LrcLibResult?> FindAsync(Track track, CancellationToken ct = default)
    {
        if (!Enabled) return null;

        await _gate.WaitAsync(ct);
        try
        {
            // 1. The exact hit. A 404 is common and means "not at that length", not "no such song".
            var query = $"?artist_name={Escape(track.Artist)}&track_name={Escape(track.Title)}" +
                        $"&album_name={Escape(track.Album)}" +
                        $"&duration={(int)Math.Round(track.Duration.TotalSeconds)}";

            var exact = await GetAsync<LrcLibResult>("/api/get" + query, ct);
            if (Matching.IsUsable(exact)) return Found(exact);

            // 2. The fuzzy hit, filtered by Matching. Only after a 404 — never speculatively.
            var search = await GetAsync<LrcLibResult[]>(
                $"/api/search?track_name={Escape(track.Title)}&artist_name={Escape(track.Artist)}", ct);
            if (search is null) return null;

            var best = Matching.Best(track, search);
            if (best is not null) return Found(best);

            Misses++;
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Whether the last call failed cleanly ("not there") rather than badly ("could not ask").</summary>
    /// <remarks>
    ///     Read straight after <see cref="FindAsync" /> under the same logical operation. It is a field
    ///     rather than a tuple because every caller needs it and only after a <c>null</c>.
    /// </remarks>
    public bool LastWasClean { get; private set; } = true;

    public object Snapshot()
    {
        return new
        {
            enabled = Enabled,
            url = _baseUrl,
            requests = Requests,
            hits = Hits,
            misses = Misses,
            rateLimited = RateLimited,
            pausedUntil = PausedUntil,
            lastError = LastError
        };
    }

    private LrcLibResult Found(LrcLibResult? result)
    {
        Hits++;
        return result!;
    }

    private async Task<T?> GetAsync<T>(string path, CancellationToken ct) where T : class
    {
        if (PausedUntil > DateTimeOffset.UtcNow)
        {
            LastWasClean = false;
            return null;
        }

        var http = _factory.CreateClient("lrclib");
        using var request = new HttpRequestMessage(HttpMethod.Get, _baseUrl + path);
        request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);

        try
        {
            Requests++;
            using var response = await http.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                // Obeyed rather than retried: the sweep parks and the on-demand lookup fails without
                // writing a row, so the track is tried again rather than remembered as a miss.
                RateLimited++;
                var after = response.Headers.RetryAfter?.Delta ??
                            TimeSpan.FromSeconds(response.Headers.RetryAfter?.Date is { } date
                                ? Math.Max(0, (date - DateTimeOffset.UtcNow).TotalSeconds)
                                : 60);

                PausedUntil = DateTimeOffset.UtcNow + after;
                LastError = $"429 from LRCLIB; paused until {PausedUntil:O}";
                LastWasClean = false;
                _logger.Warning("LRCLIB rate-limited us; pausing until {Until}", PausedUntil);
                return null;
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                LastWasClean = true;
                return null;
            }

            response.EnsureSuccessStatusCode();
            LastWasClean = true;
            return await response.Content.ReadFromJsonAsync<T>(Json, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LastError = e.Message;
            LastWasClean = false;
            _logger.Warning(e, "LRCLIB request {Path} failed", path);
            return null;
        }
    }

    private static string Escape(string? value)
    {
        return Uri.EscapeDataString(value?.Trim() ?? string.Empty);
    }
}
