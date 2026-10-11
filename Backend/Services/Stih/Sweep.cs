using System.Globalization;
using Microsoft.Extensions.Options;
using ILogger = Serilog.ILogger;

namespace Stih;

/// <summary>
///     The backfill: what turns "usable" into a library that fills up on its own.
/// </summary>
/// <remarks>
///     Progress is gaida-local's index and <c>Lyrics.db</c> themselves. There is no resume marker to
///     corrupt — a pod restarted mid-sweep asks for a page and gets the tracks it had not reached.
/// </remarks>
public sealed class Sweep(
    Lyrics lyrics,
    LrcLib lrcLib,
    IHttpClientFactory factory,
    IOptions<StihOptions> options,
    ILogger logger) : BackgroundService
{
    /// <summary>How long the sweep lets the stack come up before asking it about tracks.</summary>
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(20);

    /// <summary>Tracks fetched per page of the library's backlog.</summary>
    private const int PageSize = 200;

    private readonly ILogger _logger = logger.ForContext<Sweep>();

    private int Looked { get; set; }
    private int Found { get; set; }
    private int Missed { get; set; }
    private DateTimeOffset? LastPage { get; set; }
    private string? LastTrack { get; set; }

    private bool Enabled => options.Value.SweepEnabled;
    private TimeSpan Delay => options.Value.SweepDelay;
    private TimeSpan Idle => options.Value.SweepIdle;
    private int RetryDays => options.Value.SweepRetryDays;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
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
        await Task.Delay(StartupGrace, stoppingToken);
        _logger.Information("Lyrics sweep started, one track every {Delay}", Delay);

        while (!stoppingToken.IsCancellationRequested)
            try
            {
                await PassAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                _logger.Error(e, "The lyrics sweep stumbled; retrying in a minute");
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
    }

    /// <summary>One page of the backlog, a track at a time. An empty page means the library is done for now.</summary>
    private async Task PassAsync(CancellationToken ct)
    {
        var page = await PageAsync(ct);
        LastPage = DateTimeOffset.UtcNow;

        if (page.Count == 0)
        {
            // New imports and expired retry windows are what the next pass finds.
            _logger.Information("Nothing left to look up; sleeping {Idle}", Idle);
            await Task.Delay(Idle, ct);
            return;
        }

        foreach (var row in page)
        {
            if (ct.IsCancellationRequested) return;
            await OneAsync(row, ct);
            await Task.Delay(Delay, ct);
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
        var (found, clean) = await lrcLib.FindAsync(track, ct);
        if (found is null)
        {
            // A failure records nothing and the track simply comes back in a later page.
            if (!clean) return;

            Missed++;
            await lyrics.RecordMissAsync(track, ct);
            return;
        }

        Found++;
        var (content, kind) = found.Best();

        await lyrics.StoreAsync(track, content, kind, LyricsOrigin.Lrclib, false, ct);
    }

    /// <summary>
    ///     A page of work from gaida-local. The names and the length come with it, so the sweep never
    ///     calls <c>/resolve</c> — one request per two hundred tracks rather than one per track.
    /// </summary>
    private async Task<IReadOnlyList<MissingLyricsDto>> PageAsync(CancellationToken ct)
    {
        if (options.Value.LocalUrl?.TrimEnd('/') is not { Length: > 0 } local) return [];

        var http = factory.CreateClient("pods");
        return await http.GetFromJsonAsync<List<MissingLyricsDto>>(
            $"{local}/lyrics/missing?take={PageSize}&retryDays={RetryDays}", ct) ?? [];
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
