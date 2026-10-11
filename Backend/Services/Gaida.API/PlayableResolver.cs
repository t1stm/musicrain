using Gaida.Core;
using Gaida.Core.Platforms;
using Gaida.Core.Utils;
using Gaida.Infrastructure.Platforms;
using Microsoft.Extensions.Options;

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
    IOptions<GaidaApiOptions> options,
    ILogger<PlayableResolver> logger)
{
    public int Concurrency => Math.Max(1, options.Value.ResolveConcurrency);

    /// <summary>
    ///     Resolves a stream of metadata-only results. Tracks nothing playable was found for are dropped
    ///     rather than returned unplayable.
    /// </summary>
    public IAsyncEnumerable<PlatformResult> Resolve(IAsyncEnumerable<PlatformResult> source,
        CancellationToken cancellationToken)
    {
        return source.SelectParallel(Concurrency, ResolveOne, cancellationToken);
    }

    /// <summary>What a classified query plays as: a playlist's entries or a keyword search's hits, made playable.</summary>
    public IAsyncEnumerable<PlatformResult> Discover(ClassifyClaim claim, CancellationToken cancellationToken)
    {
        var manager = managerService.Manager;

        // Both of these are already guarded per platform inside AudioManager, so one unreachable pod
        // shortens the stream instead of tearing the response.
        var results = claim.Kind == QueryType.Playlist
            ? manager.SearchPlaylist(claim.Query, cancellationToken)
            : manager.SearchKeywords(claim.Query, cancellationToken);

        // Every stream goes through the resolver, not just a metadata-only playlist: the Spotify pod answers
        // keyword searches too, so a name can now arrive in the middle of an ordinary search rather than only
        // from a playlist. Results that are already playable pass straight through without holding the stream
        // up — see SelectParallel — so an ordinary search pays nothing for this.
        return Resolve(results, cancellationToken)
            // A Spotify hit resolved against YouTube lands on the video YouTube itself just returned, one
            // entry earlier in this same stream. Without this the client renders it twice.
            .DistinctBy(result => result.Id);
    }

    /// <summary>One ID, made playable; <c>null</c> when nothing has it.</summary>
    public async Task<PlatformResult?> LookUpAsync(string id, CancellationToken cancellationToken)
    {
        // A Spotify link resolves to a name, not to audio — the playable track is whatever the
        // library or YouTube has for it.
        var found = await managerService.Manager.SearchId(id, cancellationToken);
        return found is null ? null : await ResolveOne(found, cancellationToken);
    }

    /// <returns>The playable result, the input when it already is one, or <c>null</c> when nothing was found.</returns>
    public async Task<PlatformResult?> ResolveOne(PlatformResult result, CancellationToken cancellationToken)
    {
        if (!managerService.NeedsResolving(result.Id)) return result;

        var name = result.OriginalTitle is { Length: > 0 } title ? title : result.Name;
        var artist = result.OriginalArtist is { Length: > 0 } artistName ? artistName : result.Artist;
        if (string.IsNullOrWhiteSpace(name)) return null;

        // /variant is exactly this question — name, artist and duration against the library — and it already
        // decides what counts as the same track.
        if (managerService.Pod(Prefix.Local) is { } local)
        {
            var variant = await local.VariantAsync(name, artist, result.Duration, cancellationToken);
            if (variant?.Result is not null && variant.Match is "same" or "variant")
                return local.ToResult(variant.Result);
        }

        var term = string.IsNullOrWhiteSpace(artist) ? name : $"{artist} {name}";

        // Deezer before YouTube: its first hit for "artist title" is the release itself, where YouTube's is
        // whatever was uploaded under that name. Skipped when Deezer is metadata-only in this deployment
        // (no ARL, so Platforms__N__Resolve is on) — resolving one name into another resolves nothing.
        if (!managerService.NeedsResolving(Prefix.Deezer) && managerService.Pod(Prefix.Deezer) is { } deezer)
            await foreach (var hit in deezer.SearchKeywords(term, cancellationToken))
                return hit;

        if (managerService.Pod(Prefix.YouTube) is { } youTube)
            await foreach (var hit in youTube.SearchKeywords(term, cancellationToken))
                return hit;

        logger.NothingPlayable(result.Id, artist, name);
        return null;
    }
}
