using Gaida.Core.Platforms;
using Gaida.Core.Utils;

namespace Gaida.API;

/// <summary>
///     Turns results that are only names — a Spotify track has no audio and no downloader — into results that
///     can be played, by searching the platforms that do have content. The library first, since a local copy
///     beats an upload of the same track; then Deezer, whose catalogue is the studio recording rather than
///     somebody's upload of it; then YouTube's first hit, which is the only source that has everything.
/// </summary>
/// <remarks>
///     One lookup per track, so a playlist is as many round trips as it has tracks. They overlap
///     (<see cref="Streaming.SelectParallel{TIn,TOut}" />) and the playlist's own order is preserved, and
///     because both ends of the pipe stream, the first track is playable long before the last one is looked
///     up. Concurrency is deliberately low: YouTube's rate limit is the ceiling here, not CPU.
/// </remarks>
public sealed class PlayableResolver(
    ManagerService managerService,
    IConfiguration configuration,
    ILogger<PlayableResolver> logger)
{
    private int Concurrency => Math.Max(1, configuration.GetValue("Resolve:Concurrency", 4));

    /// <summary>
    ///     Resolves a stream of metadata-only results. Tracks nothing playable was found for are dropped
    ///     rather than returned unplayable.
    /// </summary>
    public IAsyncEnumerable<PlatformResult> Resolve(IAsyncEnumerable<PlatformResult> source,
        CancellationToken cancellationToken)
    {
        return source.SelectParallel(Concurrency, ResolveOne, cancellationToken);
    }

    /// <returns>The playable result, the input when it already is one, or <c>null</c> when nothing was found.</returns>
    public async Task<PlatformResult?> ResolveOne(PlatformResult result, CancellationToken cancellationToken)
    {
        if (!managerService.NeedsResolving(result.Id)) return result;

        var name = result.OriginalTitle is { Length: > 0 } title ? title : result.Name;
        var artist = result.OriginalArtist is { Length: > 0 } artistName ? artistName : result.Artist;
        if (string.IsNullOrWhiteSpace(name)) return null;

        var manager = managerService.Manager;

        // /variant is exactly this question — name, artist and duration against the library — and it already
        // decides what counts as the same track.
        if (manager.PlatformFor("audio://") is HttpPlatform local)
        {
            var variant = await local.VariantAsync(name, artist, result.Duration, cancellationToken);
            if (variant?.Result is not null && variant.Match is "same" or "variant")
                return local.ToResult(variant.Result);
        }

        var term = string.IsNullOrWhiteSpace(artist) ? name : $"{artist} {name}";

        // Deezer before YouTube: its first hit for "artist title" is the release itself, where YouTube's is
        // whatever was uploaded under that name. Skipped when Deezer is metadata-only in this deployment
        // (no ARL, so Platforms__N__Resolve is on) — resolving one name into another resolves nothing.
        if (!managerService.NeedsResolving("deezer://") &&
            manager.PlatformFor("deezer://") is HttpPlatform deezer)
            await foreach (var hit in deezer.SearchKeywords(term, cancellationToken))
                return hit;

        if (manager.PlatformFor("yt://") is HttpPlatform youTube)
            await foreach (var hit in youTube.SearchKeywords(term, cancellationToken))
                return hit;

        logger.LogDebug("Nothing playable for {ID} ({Artist} — {Name})", result.Id, artist, name);
        return null;
    }
}
