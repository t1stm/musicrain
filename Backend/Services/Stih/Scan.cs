using Microsoft.Extensions.Options;
using ILogger = Serilog.ILogger;

namespace Stih;

/// <summary>
///     Picks up the <c>.lrc</c> files that change in the library behind stih's back: Lirika writing one, an
///     edit from the studio copied in, a file someone drops in by hand.
/// </summary>
/// <remarks>
///     Every library <c>.lrc</c> whose mtime differs from the one recorded on its row is recorded again, so
///     a remembered miss stops hiding a file that has since appeared. A row written by anything but this scan
///     has no mtime, which is why the first run after the upgrade reads the whole library once and every run
///     after that only what changed.
/// </remarks>
public sealed class Scan(
    Lyrics lyrics,
    LyricsIndex index,
    IHttpClientFactory factory,
    IOptions<StihOptions> options,
    ILogger logger) : BackgroundService
{
    /// <summary>A walk is a few milliseconds of directory listing, so the wait is set by how soon an edit should show.</summary>
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(1);

    /// <summary>The same grace the sweep gives the pods: gaida-local is starting up too.</summary>
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(20);

    private readonly ILogger _logger = logger.ForContext<Scan>();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupGrace, stoppingToken);
            using var timer = new PeriodicTimer(Every);
            do
            {
                try
                {
                    await RunAsync(stoppingToken);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // Nothing was recorded for what failed, so the next run finds the same files again.
                    _logger.Warning(e, "The lyrics file scan failed; trying again in {Every}", Every);
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>One walk of the library.</summary>
    /// <returns>How many files were recorded.</returns>
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        var root = Path.GetFullPath(options.Value.MusicLibrary);
        if (!Directory.Exists(root)) return 0;

        var seen = index.Seen();
        var changed = new List<(string Relative, long Modified)>();
        foreach (var file in Directory.EnumerateFiles(root, "*.lrc", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file);
            var modified = File.GetLastWriteTimeUtc(file).Ticks;
            if (!seen.TryGetValue(relative, out var known) || known != modified) changed.Add((relative, modified));
        }

        if (changed.Count == 0) return 0;

        var owners = await OwnersAsync(ct);
        var recorded = 0;
        foreach (var (relative, modified) in changed)
        {
            // ponytail: an .lrc with no song beside it is looked up again on every run, owners fetch included.
            // Remember those by mtime if the library ever holds enough of them for that to matter.
            if (!owners.TryGetValue(relative, out var id)) continue;

            LyricsOrigin? source;
            try
            {
                source = IsLirika(Path.Combine(root, relative))
                    ? LyricsOrigin.Generated
                    // An edit of the file the row already names keeps its provenance; any other file is
                    // "already in the folder", which is what a null source has always meant.
                    : index.Get(id) is { } row && row.Path == relative ? row.Source : null;
            }
            catch (IOException)
            {
                continue; // gone or renamed since the walk; the next run sees what replaced it
            }

            index.Record(new LyricsRow(id, LyricsKind.Synchronized, source, LyricsVolume.Library, relative,
                DateTimeOffset.UtcNow), modified);
            // Only takes on an entry gaida-local holds as missing, which is the one that matters: the sweep
            // would otherwise ask LRCLIB for a track that already has words.
            await lyrics.StampAsync(id, LyricsKind.Synchronized, source, ct);
            recorded++;
        }

        _logger.Information("Recorded {Recorded} changed lyrics files; {Unowned} have no library song beside them",
            recorded, changed.Count - recorded);
        return recorded;
    }

    /// <summary>Lirika names itself in every file it writes, as <c>[re:musicrain/Lirika]</c>.</summary>
    private static bool IsLirika(string path)
    {
        return File.ReadLines(path).Take(20).Any(line =>
            line.StartsWith("[re:", StringComparison.OrdinalIgnoreCase) &&
            line.Contains("Lirika", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Which song each <c>.lrc</c> path belongs to, from gaida-local, which alone knows the IDs.</summary>
    private async Task<Dictionary<string, string>> OwnersAsync(CancellationToken ct)
    {
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        if (options.Value.LocalUrl?.TrimEnd('/') is not { Length: > 0 } local) return owners;

        var http = factory.CreateClient("pods");
        foreach (var owner in await http.GetFromJsonAsync<List<LyricsOwnerDto>>($"{local}/lyrics/owners", ct) ?? [])
            owners[owner.Path] = owner.Id;

        return owners;
    }
}
