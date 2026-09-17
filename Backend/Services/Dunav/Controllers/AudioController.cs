using Microsoft.AspNetCore.Mvc;

namespace Dunav.Controllers;

/// <summary>
///     Public route shape kept identical to Gaida.API's <c>Content.cs</c>: same three paths, same query
///     params. Downstream of it every request is coalesced through <see cref="CacheService" /> instead of
///     hitting Gaida.API directly.
/// </summary>
[ApiController]
public class AudioController(ILogger<AudioController> logger, CacheService cache) : ControllerBase
{
    [HttpGet]
    [Route("/Audio/DownloadRaw")]
    public async Task<IActionResult> DownloadRaw(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return NotFound();

        var key = CacheService.RawKey(id);
        var entry = await GetOrFetch(key, $"/Audio/DownloadRaw?id={Uri.EscapeDataString(id)}", out _, $"raw {id}");
        if (entry is null) return StatusCode(502);

        return await Respond(key, entry);
    }

    [HttpGet]
    [Route("/Audio/Download/{codec:required}/{bitrate:int:required}")]
    public async Task<IActionResult> Download(string codec, int bitrate, string id)
    {
        if (bitrate < 8) return BadRequest("Bitrate must be greater than 8");
        if (string.IsNullOrWhiteSpace(id)) return NotFound("No ID provided");

        var key = CacheService.EncodedKey(codec, bitrate, id);
        var entry = await GetOrFetch(key, UpstreamDownloadPath(codec, bitrate, id), out _, Label(codec, bitrate, id));
        if (entry is null) return StatusCode(502);

        return await Respond(key, entry);
    }

    /// <summary>
    ///     Starts the same upstream fetch <see cref="Download" /> would, without a body: the next Download for
    ///     the same codec/bitrate/id then finds it already running or cached.
    /// </summary>
    [HttpGet]
    [Route("/Audio/Preload/{codec:required}/{bitrate:int:required}")]
    public async Task<IActionResult> Preload(string codec, int bitrate, string id)
    {
        if (bitrate < 8) return BadRequest("Bitrate must be greater than 8");
        if (string.IsNullOrWhiteSpace(id)) return NotFound("No ID provided");

        var key = CacheService.EncodedKey(codec, bitrate, id);

        // 200 for every caller after the first: the fetch is already running or finished, and the lookup has
        // just pushed its expiry back, which is the whole of what a repeat preload can usefully do.
        if (cache.Has(key)) return Ok();

        logger.LogInformation("Preloading '{ID}' {Codec} {Bitrate}", id, codec, bitrate);
        var entry = await GetOrFetch(key, UpstreamDownloadPath(codec, bitrate, id), out var started,
            Label(codec, bitrate, id));
        if (entry is null) return StatusCode(502);

        // Two callers can reach this together and both find TryGet empty; only one of them added the
        // entry, and only that one gets the 202.
        return started ? Accepted() : Ok();
    }

    private static string UpstreamDownloadPath(string codec, int bitrate, string id)
    {
        return $"/Audio/Download/{codec}/{bitrate}?id={Uri.EscapeDataString(id)}";
    }

    /// <summary>What the cache key hashes away, kept beside it so /Admin/snapshot is readable.</summary>
    private static string Label(string codec, int bitrate, string id)
    {
        return $"{codec} {bitrate}k {id}";
    }

    private Task<CacheEntry?> GetOrFetch(string key, string upstreamPath, out bool started, string? label = null)
    {
        return cache.GetOrStartAsync(key, entry => cache.FetchAsync(entry, upstreamPath, CancellationToken.None),
            out started, label);
    }

    private async Task<IActionResult> Respond(string key, CacheEntry entry)
    {
        Response.ContentType = entry.ContentType;
        if (entry.ContentDisposition is not null)
            Response.Headers.Append("Content-Disposition", entry.ContentDisposition);
        if (entry.ETag is not null) Response.Headers.ETag = entry.ETag;
        Response.Headers.Append("Cache-Control", "public, max-age=31536000, immutable");

        // Only claim range support for a finished body: promising it mid-fetch makes players seek into a 200.
        Response.Headers.AcceptRanges = entry.Body.Closed ? "bytes" : "none";

        try
        {
            // A finished body is just a file. Handing the stream to FileStreamResult gets range parsing, the
            // 206, Content-Range, the 416 case and a real Content-Length for free, and lets the kernel do the
            // copy -- where the old path materialised the whole body on the heap twice over to answer a seek.
            if (entry.Body.Closed)
                return File(entry.Body.OpenRead(), entry.ContentType, true);

            await using var reader = entry.Body.OpenRead();
            await reader.CopyToAsync(Response.Body, HttpContext.RequestAborted);
            return new EmptyResult();
        }
        catch (FileNotFoundException)
        {
            // Eviction unlinked the body between the lookup that refreshed its expiry and our open. Narrow,
            // but reachable -- EvictOverCeiling can fire in that window. The entry has outlived its file, so
            // drop it and let the client retry into a fresh fetch.
            logger.LogInformation("Cache entry {Key} was evicted before it could be served", key);
            cache.Forget(key);
            return StatusCode(503);
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            // The client left mid-stream. Nothing to clean up: the handle closes with the request and no
            // other reader is affected.
            return new EmptyResult();
        }
    }
}
