using System.Net.Http.Json;
using ILogger = Serilog.ILogger;

namespace Stih;

/// <summary>
///     The backfill: what turns "usable" into a library that fills up on its own.
/// </summary>
/// <remarks>
///     Progress is <c>Info.json</c> and <c>Lyrics.json</c> themselves. There is no resume marker to
///     corrupt — a pod restarted mid-sweep asks for a page and gets the tracks it had not reached.
/// </remarks>
public sealed class Sweep(
    Lyrics lyrics,
    LrcLib lrcLib,
    IHttpClientFactory factory,
    IConfiguration configuration,
    ILogger logger) : BackgroundService
{
    private readonly ILogger _logger = logger.ForContext<Sweep>();

    public int Looked { get; private set; }
    public int Found { get; private set; }
    public int Missed { get; private set; }
    public DateTimeOffset? LastPage { get; private set; }
    public string? LastTrack { get; private set; }

    private bool Enabled => configuration["LYRICS_SWEEP"] is not { } value ||
                            !bool.TryParse(value, out var on) || on;

    private TimeSpan Delay => TimeSpan.FromMilliseconds(
        int.TryParse(configuration["LYRICS_SWEEP_DELAY_MS"], out var ms) ? Math.Max(0, ms) : 1000);

    private TimeSpan Idle => TimeSpan.FromHours(
        double.TryParse(configuration["LYRICS_SWEEP_IDLE_HOURS"], out var hours) ? Math.Max(0.1, hours) : 6);

    private int RetryDays => int.TryParse(configuration["LYRICS_RETRY_DAYS"], out var days) ? days : 30;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!Enabled)
        {
            // The on-demand path keeps working, and the library fills up from what people actually listen
            // to -- arguably the better order for a first run.
            _logger.Information("LYRICS_SWEEP is off: the library fills in from what people play");
            return;
        }

        if (!lrcLib.Enabled)
        {
            _logger.Information("LRCLIB_URL is empty, so there is nothing for the sweep to ask");
            return;
        }

        // The host has to be listening before this starts asking other pods about tracks: they are
        // starting up too, and a page fetched into a connection-refused is a page wasted.
        await Task.Delay(TimeSpan.FromSeconds(20), ct);
        _logger.Information("Lyrics sweep started, one track every {Delay}", Delay);

        while (!ct.IsCancellationRequested)
            try
            {
                var page = await PageAsync(ct);
                LastPage = DateTimeOffset.UtcNow;

                if (page.Count == 0)
                {
                    // The library is done. New imports and expired retry windows are what the next pass finds.
                    _logger.Information("Nothing left to look up; sleeping {Idle}", Idle);
                    await Task.Delay(Idle, ct);
                    continue;
                }

                foreach (var row in page)
                {
                    if (ct.IsCancellationRequested) return;
                    await OneAsync(row, ct);
                    await Task.Delay(Delay, ct);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                _logger.Error(e, "The lyrics sweep stumbled; retrying in a minute");
                await Task.Delay(TimeSpan.FromMinutes(1), ct);
            }
    }

    private async Task OneAsync(MissingLyricsDto row, CancellationToken ct)
    {
        // Parked while LRCLIB has told us to wait. The delay between tracks is still paid, so this spins
        // no faster than the sweep does normally.
        while (lrcLib.PausedUntil > DateTimeOffset.UtcNow && !ct.IsCancellationRequested)
            await Task.Delay(TimeSpan.FromSeconds(5), ct);

        if (Tracks.FromMissing(row) is not { } track) return;

        LastTrack = track.Id;
        Looked++;

        // The LRCLIB gate is released between items by FindAsync itself, so a listener waiting on a
        // lookup is behind at most one request rather than behind the whole sweep.
        var found = await lrcLib.FindAsync(track, ct);
        if (found is null)
        {
            // A failure records nothing and the track simply comes back in a later page.
            if (!lrcLib.LastWasClean) return;

            Missed++;
            await lyrics.RecordMissAsync(track, ct);
            return;
        }

        Found++;
        var content = found.SyncedLyrics ?? found.PlainLyrics ?? string.Empty;
        var kind = found.SyncedLyrics is not null ? LyricsKind.Synchronized : LyricsKind.Unsynchronized;

        await lyrics.StoreAsync(track, content, kind, LyricsOrigin.LRCLIB, false, ct);
    }

    /// <summary>
    ///     A page of work from gaida-local. The names and the length come with it, so the sweep never
    ///     calls <c>/resolve</c> — one request per two hundred tracks rather than one per track.
    /// </summary>
    private async Task<IReadOnlyList<MissingLyricsDto>> PageAsync(CancellationToken ct)
    {
        if (configuration["Local:Url"]?.TrimEnd('/') is not { Length: > 0 } local) return [];

        var http = factory.CreateClient("pods");
        return await http.GetFromJsonAsync<List<MissingLyricsDto>>(
            $"{local}/lyrics/missing?take=200&retryDays={RetryDays}", ct) ?? [];
    }

    public object Snapshot()
    {
        return new
        {
            enabled = Enabled,
            delayMs = Delay.TotalMilliseconds,
            idleHours = Idle.TotalHours,
            looked = Looked,
            found = Found,
            missed = Missed,
            lastPage = LastPage,
            lastTrack = LastTrack
        };
    }
}
