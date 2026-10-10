using System.Diagnostics;
using Gaida.API.Contracts;
using Gaida.Core;
using Gaida.Core.Utils;
using Gaida.Infrastructure.FFmpeg;
using Gaida.Infrastructure.Platforms;
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

        var cancellationToken = HttpContext.RequestAborted;

        ClassifyClaim claim;
        try
        {
            claim = await managerService.ClassifyAsync(query, cancellationToken);
            if (claim.Error is not null)
            {
                // A pod recognised the query as its own but rejected it (e.g. a malformed yt:// id). Discovery
                // stays a valid empty result rather than surfacing the resolver's 400 here — that belongs to
                // /Audio/FindQueryType, which callers use before they commit to a search.
                logger.LogWarning("Classify rejected {Query}: {Error}", query, claim.Error);
                return Ok(Array.Empty<SearchResultDto>());
            }

            if (claim.Kind == QueryType.Id)
            {
                var mapped = await LookUp(claim.Query, resolver, cancellationToken);
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

        return Ok(this.Mapped(resolver.Discover(claim, cancellationToken), configuration, environment));
    }

    /// <summary>
    ///     Many IDs at once, which is how the listening history shows its rows: it stores IDs and nothing
    ///     else. Each result is paired with the ID that was asked for, because the resolver can hand back
    ///     another — a metadata-only <c>deezer://</c> comes back as the library's or YouTube's copy. An ID
    ///     nothing resolves is left out, and so is one whose lookup failed: a pod that throws costs its own
    ///     rows, not the answer.
    /// </summary>
    [HttpGet]
    [Route("/Audio/Resolve")]
    [Produces("application/json")]
    [ProducesResponseType<IReadOnlyList<ResolvedDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorBody>(StatusCodes.Status400BadRequest)]
    public IActionResult Resolve([FromQuery] string[] id, [FromServices] PlayableResolver resolver)
    {
        if (id.Length is 0 or > 50 || id.Any(one => !one.Contains("://", StringComparison.Ordinal)))
            return BadRequest(new ApiErrorBody(new ApiError("invalid_query",
                "Send 1 to 50 ids, each like audio://… or yt://….")));

        logger.LogInformation("Resolving {Count} ids", id.Length);

        // The same budget the resolver keeps, since most of these are a YouTube or Deezer lookup.
        return Ok(id.Distinct(StringComparer.Ordinal).AsAsync().SelectParallel<string, ResolvedDto>(
            resolver.Concurrency, async (one, cancellationToken) =>
            {
                try
                {
                    return await LookUp(one, resolver, cancellationToken) is { } result
                        ? new ResolvedDto(one, result)
                        : null;
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    logger.LogWarning(exception, "Resolving {Id} failed", one);
                    return null;
                }
            }, HttpContext.RequestAborted));
    }

    /// <summary>One ID, made playable and mapped; <c>null</c> when nothing has it.</summary>
    private async Task<SearchResultDto?> LookUp(string id, PlayableResolver resolver,
        CancellationToken cancellationToken)
    {
        var found = await resolver.LookUpAsync(id, cancellationToken);
        return found is null ? null : DiscoveryResultMapper.Map(found, Request, configuration, environment);
    }

    [HttpGet]
    [Route("/Audio/RandomResults")]
    [Produces("application/json")]
    [ProducesResponseType<IReadOnlyList<SearchResultDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorBody>(StatusCodes.Status400BadRequest)]
    public IActionResult RandomResults(
        [FromServices] ManagerService managerService, int count = 10, double? youTubeShare = null)
    {
        if (count is < 1 or > 200)
            return BadRequest(new ApiErrorBody(new ApiError("invalid_count", "count must be between 1 and 200.")));

        var share = youTubeShare ?? configuration.GetValue("Random:Shares:youtube", 0.4);
        if (share is < 0 or > 1 or double.NaN)
            return BadRequest(new ApiErrorBody(new ApiError("invalid_share", "youTubeShare must be between 0 and 1.")));

        logger.LogInformation("Returning {Count} random results with a {Share} YouTube share", count, share);
        return Ok(this.Mapped(managerService.RandomResults(count, share, HttpContext.RequestAborted),
            configuration, environment));
    }

    [HttpGet]
    [Route("/Audio/DownloadRaw")]
    [Produces("audio/ogg", "audio/mpeg", "audio/aac", "audio/flac", "audio/mka", "audio/webm", "text/plain")]
    public async Task<IActionResult> DownloadRaw(string id, [FromServices] ManagerService managerService)
    {
        if (string.IsNullOrWhiteSpace(id)) return NotFound();
        logger.LogInformation("Downloading Raw '{ID}'", id);

        var start = Stopwatch.GetTimestamp();
        var platform = PlatformFor(managerService, id);
        if (platform is null) return NotFound();

        // No format hint: a raw request names no codec, so a pod with a choice serves whatever it
        // already has rather than fetching a bigger copy nobody asked for.
        using var upstream = await platform.GetContentResponseAsync(id, cancellationToken: HttpContext.RequestAborted);
        if (upstream is null) return NotFound();

        RelayContentHeaders(upstream);
        SetCacheHeaders($"raw-{PlatformId.FileId(id)}");

        await upstream.Content.CopyToAsync(Response.Body, HttpContext.RequestAborted);

        logger.LogInformation("Finishing '{ID}' took '{Duration}'", id, Stopwatch.GetElapsedTime(start));
        return new EmptyResult();
    }

    [HttpGet]
    [Route("/Audio/Download/{codec:required}/{bitrate:int:required}")]
    [Produces("audio/ogg", "audio/mp3", "audio/aac", "audio/flac", "audio/mka", "audio/webm", "text/plain")]
    public async Task<IActionResult> Download(string codec, int bitrate, string id,
        [FromServices] ManagerService managerService)
    {
        if (bitrate < 8) return BadRequest("Bitrate must be at least 8");
        if (string.IsNullOrWhiteSpace(id)) return NotFound("No ID provided");
        logger.LogInformation("Downloading '{ID}' {Codec} {Bitrate}", id, codec, bitrate);

        if (Encoding(codec) is not { } encoding) return BadRequest($"Unknown codec '{codec}'");

        var platform = PlatformFor(managerService, id);
        if (platform is null) return NotFound("Search resulted in error");

        // Encoding FLAC out of a lossy source is a bigger file that sounds no better, so a pod that can
        // choose (the Deezer one, which otherwise downloads MP3 320) is told to fetch its lossless copy.
        // Every other codec here is lossy anyway and takes whatever the pod already has.
        var sourceFormat = string.Equals(codec, "FLAC", StringComparison.OrdinalIgnoreCase) ? "flac" : null;

        using var upstream = await platform.GetContentResponseAsync(id, sourceFormat, HttpContext.RequestAborted);
        if (upstream is null) return NotFound("Search resulted in error");

        var fileId = PlatformId.FileId(id);
        Response.ContentType = encoding.ContentType;
        Response.Headers.Append("Content-Disposition", $"inline; filename={fileId}.{encoding.Format}");
        SetCacheHeaders($"v2-{encoding.ContentType}-{bitrate}-{fileId}");

        await using var source = await upstream.Content.ReadAsStreamAsync(HttpContext.RequestAborted);
        var ffmpegArguments = $"-c:a {encoding.Codec} -b:a {bitrate}k -vn -d copy -f {encoding.Format}";
        await FFmpegEncoder.EncodeAsync(source, Response.Body, ffmpegArguments, HttpContext.RequestAborted);
        return new EmptyResult();
    }

    /// <summary>
    ///     The ffmpeg arguments and response content type for a codec name, in any case, or <c>null</c> for one
    ///     this does not encode. There used to be a default, Opus in Matroska, but ffmpeg has no <c>mka</c>
    ///     muxer, so it failed every time — and a lowercase <c>opus</c> fell through to it.
    /// </summary>
    public static AudioEncoding? Encoding(string codec)
    {
        return codec.ToUpperInvariant() switch
        {
            "OPUS" => new AudioEncoding("audio/ogg", "libopus", "ogg"),
            "VORBIS" => new AudioEncoding("audio/ogg", "libvorbis", "ogg"),
            "AAC" => new AudioEncoding("audio/aac", "aac", "adts"),
            "FLAC" => new AudioEncoding("audio/flac", "flac", "flac"),
            "MP3" => new AudioEncoding("audio/mpeg", "libmp3lame", "mp3"),
            _ => null
        };
    }

    /// <summary>
    ///     What a codec name encodes to: the response's content type, ffmpeg's encoder and its muxer. The muxer
    ///     doubles as the file extension.
    /// </summary>
    public sealed record AudioEncoding(string ContentType, string Codec, string Format);

    /// <summary>The platform pod that owns <paramref name="id" />'s protocol prefix, or <c>null</c> when none does.</summary>
    private static HttpPlatform? PlatformFor(ManagerService managerService, string id)
    {
        return PlatformId.TrySplit(id, out var prefix, out _) ? managerService.Pod(prefix) : null;
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