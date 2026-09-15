using System.Diagnostics;
using Gaida.API.Contracts;
using Gaida.Core;
using Gaida.Core.FFmpeg;
using Gaida.Core.Platforms;
using Gaida.Core.Utils;
using Microsoft.AspNetCore.Mvc;

namespace Gaida.API.Controllers;

[ApiController]
[Route("[controller]")]
public class Content(ILogger<Content> logger, IConfiguration configuration, IHostEnvironment environment)
    : ControllerBase
{
    /// <summary>
    ///     Results are serialised as they arrive, so library hits render while YouTube is still being asked
    ///     and a long playlist fills track by track. Everything that can still fail with a status code —
    ///     classify, and the single-ID lookup — happens before the sequence is handed over.
    /// </summary>
    [HttpGet]
    [Route("/Audio/Search")]
    [Produces("application/json")]
    [ProducesResponseType<IReadOnlyList<SearchResultDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Search(string? query, [FromServices] ManagerService managerService,
        [FromServices] PlayableResolver resolver)
    {
        if (string.IsNullOrWhiteSpace(query)) return Ok(Array.Empty<SearchResultDto>());
        logger.LogInformation("Searching for {Query}", query);

        var manager = managerService.Manager;
        var cancellationToken = HttpContext.RequestAborted;

        ClassifyClaim claim;
        try
        {
            claim = await manager.ClassifyAsync(query, cancellationToken);
            if (claim.Error is not null)
            {
                // A pod recognised the query as its own but rejected it (e.g. a malformed yt:// id). Discovery
                // stays a valid empty result rather than surfacing the resolver's 400 here — that belongs to
                // /Audio/FindQueryType, which callers use before they commit to a search.
                logger.LogWarning("Classify rejected {Query}: {Error}", query, claim.Error);
                return Ok(Array.Empty<SearchResultDto>());
            }

            if (claim.Kind == QueryType.ID)
            {
                var found = await manager.SearchID(claim.Query, cancellationToken);

                // A Spotify link resolves to a name, not to audio — the playable track is whatever the
                // library or YouTube has for it.
                if (found is not null) found = await resolver.ResolveOne(found, cancellationToken);

                var mapped = found is null
                    ? null
                    : DiscoveryResultMapper.Map(found, Request, configuration, environment);
                return Ok(mapped is null ? Array.Empty<SearchResultDto>() : [mapped]);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Discovery remains a valid JSON response even when an upstream provider is unavailable.
            logger.LogError(exception, "Search failed for {Query}", query);
            return Ok(Array.Empty<SearchResultDto>());
        }

        // Both of these are already guarded per platform inside AudioManager, so one unreachable pod
        // shortens the stream instead of tearing the response.
        var results = claim.Kind == QueryType.Playlist
            ? manager.SearchPlaylist(claim.Query, cancellationToken)
            : manager.SearchKeywords(claim.Query, cancellationToken);

        // Every stream goes through the resolver, not just a metadata-only playlist: the Spotify pod answers
        // keyword searches too, so a name can now arrive in the middle of an ordinary search rather than only
        // from a playlist. Results that are already playable pass straight through without holding the stream
        // up — see SelectParallel — so an ordinary search pays nothing for this.
        results = resolver.Resolve(results, cancellationToken)
            // A Spotify hit resolved against YouTube lands on the video YouTube itself just returned, one
            // entry earlier in this same stream. Without this the client renders it twice.
            .DistinctBy(result => result.ID);

        return Ok(this.Mapped(results, configuration, environment));
    }

    [HttpGet]
    [Route("/Audio/RandomResults")]
    [Produces("application/json")]
    [ProducesResponseType<IReadOnlyList<SearchResultDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorBody>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RandomResults(
        [FromServices] ManagerService managerService, int count = 10, double? youTubeShare = null)
    {
        if (count is < 1 or > 200)
            return BadRequest(new ApiErrorBody(new ApiError("invalid_count", "count must be between 1 and 200.")));

        var share = youTubeShare ?? configuration.GetValue("Random:Shares:youtube", 0.4);
        if (share is < 0 or > 1 or double.NaN)
            return BadRequest(new ApiErrorBody(new ApiError("invalid_share", "youTubeShare must be between 0 and 1.")));

        logger.LogInformation("Returning {Count} random results with a {Share} YouTube share", count, share);
        var manager = managerService.Manager;
        var cancellationToken = HttpContext.RequestAborted;

        // Randomized rounding preserves the requested share over time while allowing either source
        // to be selected for small requests (for example, count=1 chooses YouTube 40% of the time).
        var exactYouTubeCount = count * share;
        var youTubeCount = (int)Math.Floor(exactYouTubeCount);
        if (Random.Shared.NextDouble() < exactYouTubeCount - youTubeCount)
            youTubeCount++;

        var youTube = manager.PlatformFor("yt://") is HttpPlatform youTubePlatform
            ? youTubePlatform.GetRandomResults(youTubeCount, cancellationToken)
            : Array.Empty<PlatformResult>().AsAsync();

        // ponytail: local is asked for the full count rather than the shortfall. It answers from an in-memory
        // shuffle, so over-asking is free, and whatever YouTube turns out to be short of is already on its
        // way — which is the only way a backfill can work when neither source has finished yet.
        var local = manager.PlatformFor("audio://") is HttpPlatform localPlatform
            ? localPlatform.GetRandomResults(count, cancellationToken)
            : Array.Empty<PlatformResult>().AsAsync();

        return Ok(this.Mapped(youTube.RandomMerge(youTubeCount, local, count, cancellationToken),
            configuration, environment));
    }

    [HttpGet]
    [Route("/Audio/DownloadRaw")]
    [Produces("audio/ogg", "audio/mpeg", "audio/aac", "audio/flac", "audio/mka", "audio/webm", "text/plain")]
    public async Task<IActionResult> DownloadRaw(string id, [FromServices] ManagerService managerService)
    {
        if (string.IsNullOrWhiteSpace(id)) return NotFound();
        logger.LogInformation("Downloading Raw '{Id}'", id);

        var start = Stopwatch.GetTimestamp();
        var platform = PlatformFor(managerService.Manager, id);
        if (platform is null) return NotFound();

        // No format hint: a raw request names no codec, so a pod with a choice serves whatever it
        // already has rather than fetching a bigger copy nobody asked for.
        using var upstream = await platform.GetContentResponseAsync(id, cancellationToken: HttpContext.RequestAborted);
        if (upstream is null) return NotFound();

        RelayContentHeaders(upstream);
        SetCacheHeaders($"raw-{FileId(id)}");

        await upstream.Content.CopyToAsync(Response.Body, HttpContext.RequestAborted);

        logger.LogInformation("Finishing '{Id}' took '{Duration}'", id, Stopwatch.GetElapsedTime(start));
        return new EmptyResult();
    }

    [HttpGet]
    [Route("/Audio/Download/{codec:required}/{bitrate:int:required}")]
    [Produces("audio/ogg", "audio/mp3", "audio/aac", "audio/flac", "audio/mka", "audio/webm", "text/plain")]
    public async Task<IActionResult> Download(string codec, int bitrate, string id,
        [FromServices] ManagerService managerService)
    {
        if (bitrate < 8) return BadRequest("Bitrate must be greater than 8");
        if (string.IsNullOrWhiteSpace(id)) return NotFound("No ID provided");
        logger.LogInformation("Downloading '{Id}' {Codec} {Bitrate}", id, codec, bitrate);

        var (contentType, ffmpegCodec, ffmpegOutputFormat) = Encoding(codec);

        var platform = PlatformFor(managerService.Manager, id);
        if (platform is null) return NotFound("Search resulted in error");

        // Encoding FLAC out of a lossy source is a bigger file that sounds no better, so a pod that can
        // choose (the Deezer one, which otherwise downloads MP3 320) is told to fetch its lossless copy.
        // Every other codec here is lossy anyway and takes whatever the pod already has.
        var sourceFormat = string.Equals(codec, "FLAC", StringComparison.OrdinalIgnoreCase) ? "flac" : null;

        using var upstream = await platform.GetContentResponseAsync(id, sourceFormat, HttpContext.RequestAborted);
        if (upstream is null) return NotFound("Search resulted in error");

        var fileId = FileId(id);
        Response.ContentType = contentType;
        Response.Headers.Append("Content-Disposition", $"inline; filename={fileId}.{ffmpegOutputFormat[3..]}");
        SetCacheHeaders($"v2-{contentType}-{bitrate}-{fileId}");

        await using var source = await upstream.Content.ReadAsStreamAsync(HttpContext.RequestAborted);
        var ffmpegArguments = $"{ffmpegCodec} -b:a {bitrate}k -vn -d copy {ffmpegOutputFormat}";
        await FFmpegEncoder.EncodeAsync(source, Response.Body, ffmpegArguments, HttpContext.RequestAborted);
        return new EmptyResult();
    }

    /// <summary>The ffmpeg arguments and response content type for a codec name, defaulting to Opus in Matroska.</summary>
    private static (string ContentType, string FfmpegCodec, string OutputFormat) Encoding(string codec)
    {
        return codec switch
        {
            "Opus" => ("audio/ogg", "-c:a libopus", "-f ogg"),
            "Vorbis" => ("audio/ogg", "-c:a libvorbis", "-f ogg"),
            "AAC" => ("audio/aac", "-c:a aac", "-f adts"),
            "FLAC" => ("audio/flac", "-c:a flac", "-f flac"),
            "MP3" => ("audio/mpeg", "-c:a libmp3lame", "-f mp3"),
            _ => ("audio/mka", "-c:a libopus", "-f mka")
        };
    }

    /// <summary>The platform pod that owns <paramref name="id" />'s protocol prefix, or <c>null</c> when none does.</summary>
    private static HttpPlatform? PlatformFor(AudioManager manager, string id)
    {
        var separator = id.IndexOf("://", StringComparison.Ordinal);
        if (separator < 1) return null;
        return manager.PlatformFor(id[..(separator + 3)]) as HttpPlatform;
    }

    /// <summary>The ID without its platform protocol, safe to put in a header.</summary>
    private static string FileId(string id)
    {
        var separator = id.IndexOf("://", StringComparison.Ordinal);
        var value = separator >= 0 ? id[(separator + 3)..] : id;
        return Uri.EscapeDataString(value);
    }

    private void RelayContentHeaders(HttpResponseMessage upstream)
    {
        Response.ContentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
        if (upstream.Content.Headers.ContentDisposition is { } disposition)
            Response.Headers.ContentDisposition = disposition.ToString();
    }

    private void SetCacheHeaders(string etag)
    {
        Response.Headers.Append("Cache-Control", "public, max-age=31536000, immutable");
        Response.Headers.ETag = $"\"{etag}\"";
    }

}