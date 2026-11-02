using System.Text.Json;
using Dom.Store;
using Microsoft.Extensions.Options;

namespace Dom;

/// <summary>
///     Once a day, brings every playlist's saved tracks up to what their IDs play as now: a retag in the
///     library, an artist Deezer fixed, a cover that moved. Asks Gaida.API's <c>/Audio/Resolve</c>, the route
///     the History page shows its rows through, so a playlist and the history agree on a track's name.
/// </summary>
/// <remarks>
///     Unset <c>Gaida:Url</c> skips it. Fifty IDs a request, one request at a time: the run is a few minutes
///     of background work, and gaida-api's YouTube and Deezer budgets are shared with people searching.
///     <para>
///         An operator's edit to a library song does not wait for the day: Oko sends its ID to
///         <c>/Admin/refresh-tracks</c>, which runs <see cref="RefreshAsync" /> on that one.
///     </para>
/// </remarks>
public sealed class PlaylistRefresh(
    DomStore store,
    IHttpClientFactory http,
    IOptions<DomOptions> options,
    ILogger<PlaylistRefresh> log) : BackgroundService
{
    /// <summary>The named client gaida-api is called with.</summary>
    public const string Gaida = "gaida";

    private static readonly TimeSpan Every = TimeSpan.FromDays(1);

    /// <summary>gaida-api is starting up too, and its pods behind it.</summary>
    private static readonly TimeSpan StartupGrace = TimeSpan.FromMinutes(1);

    /// <summary><c>/Audio/Resolve</c>'s limit.</summary>
    private const int Chunk = 50;

    private string? GaidaUrl => options.Value.GaidaUrl?.TrimEnd('/') is { Length: > 0 } url ? url : null;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (GaidaUrl is null)
        {
            log.RefreshOff();
            return;
        }

        try
        {
            // ponytail: every boot runs it, so a day of restarts is a day of runs. Keep the last run's time
            // in dom.db if deploys ever come often enough for that to show on gaida-api.
            await Task.Delay(StartupGrace, stoppingToken);
            using var timer = new PeriodicTimer(Every);
            do
            {
                try
                {
                    await RefreshAsync(store.TrackIds(), stoppingToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    log.RefreshFailed(exception);
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    /// <summary>
    ///     Asks what <paramref name="ids" /> play as now and renames them in every playlist that holds them. An
    ///     ID no playlist holds is not asked about.
    /// </summary>
    /// <returns>How many saved tracks changed.</returns>
    public async Task<int> RefreshAsync(IEnumerable<string> ids, CancellationToken ct)
    {
        if (GaidaUrl is not { } gaida) return 0;

        var held = store.TrackIds().Intersect(ids, StringComparer.Ordinal).ToList();
        if (held.Count == 0) return 0;

        var fresh = new Dictionary<string, TrackSnapshot>(StringComparer.Ordinal);
        var client = http.CreateClient(Gaida);

        foreach (var chunk in held.Chunk(Chunk))
            try
            {
                var query = string.Join('&', chunk.Select(id => "id=" + Uri.EscapeDataString(id)));
                var answer = await client.GetFromJsonAsync<List<Resolved>>($"{gaida}/Audio/Resolve?{query}", ct);
                foreach (var resolved in answer ?? []) fresh[resolved.Id] = resolved.Result;
            }
            catch (Exception exception) when (!ct.IsCancellationRequested
                                              && exception is HttpRequestException or TaskCanceledException
                                                  or JsonException)
            {
                // those fifty keep what they have; the rest of the run goes on
                log.RefreshChunkFailed(exception, chunk[0]);
            }

        var changed = store.RefreshTracks(fresh);
        log.Refreshed(changed, fresh.Count, held.Count);
        return changed;
    }

    /// <summary>
    ///     One answer of <c>/Audio/Resolve</c>: the ID as asked for, and what it plays as now. The result is a
    ///     whole discovery result; the snapshot's fields are the ones read out of it.
    /// </summary>
    private sealed record Resolved(string Id, TrackSnapshot Result);
}
