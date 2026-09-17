using System.Security.Cryptography;
using System.Text;
using Gaida.API.Contracts;
using Microsoft.AspNetCore.Mvc;
using SFile = System.IO.File;

namespace Gaida.API.Controllers;

/// <summary>
///     Serves track artwork from the API's own origin. An embedded Discord activity can only reach the hosts it has
///     URL mappings for, and the thumbnails the discovery endpoints hand out live on ytimg and the cover host — so
///     the API fetches them once, keeps them on disk, and hands them back under the `/api` mapping the activity has.
/// </summary>
[ApiController]
[Route("[controller]")]
public class Cover(ILogger<Cover> logger, IConfiguration configuration) : ControllerBase
{
    /// <summary>Artwork is small and immutable in practice, but a re-extracted local cover should still land within a week.</summary>
    private const string CacheControl = "public, max-age=604800";

    /// <summary>Everything upstream actually serves; anything else is stored and served back as JPEG.</summary>
    private static readonly Dictionary<string, string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg",
        [".png"] = "image/png",
        [".webp"] = "image/webp",
        [".gif"] = "image/gif"
    };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private string CacheDirectory =>
        configuration["THUMBNAIL_CACHE"] is { Length: > 0 } configured
            ? configured
            : Path.Combine(Path.GetTempPath(), "gaida-thumbnails");

    [HttpGet]
    [Route("/Audio/Cover")]
    [Produces("image/jpeg", "image/png", "image/webp", "image/gif")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorBody>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorBody>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string? id, [FromServices] ManagerService managerService)
    {
        if (string.IsNullOrWhiteSpace(id))
            return BadRequest(new ApiErrorBody(new ApiError("invalid_query", "An id is required.")));

        var directory = Path.GetFullPath(CacheDirectory);
        Directory.CreateDirectory(directory);

        // The id is a URL of sorts, so it is hashed rather than sanitised: no traversal, no illegal characters,
        // and the extension is the only thing that has to be recovered at serve time.
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id)));

        // A hit never touches the platform layer, which is what makes the cached path fast.
        var cached = Directory.EnumerateFiles(directory, key + ".*").FirstOrDefault();
        if (cached is not null)
        {
            Response.Headers.CacheControl = CacheControl;
            return PhysicalFile(cached, ContentTypeFor(cached));
        }

        var cancellationToken = HttpContext.RequestAborted;
        var result = await managerService.Manager.SearchId(id, cancellationToken);
        if (result?.ThumbnailUrl is not { Length: > 0 } source)
            return NotFound(new ApiErrorBody(new ApiError("not_found", "This id has no artwork.")));

        logger.LogInformation("Caching cover for '{ID}' from {Source}", id, source);

        try
        {
            using var upstream =
                await Http.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!upstream.IsSuccessStatusCode)
                return NotFound(new ApiErrorBody(new ApiError("not_found", "The artwork could not be fetched.")));

            var contentType = upstream.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
            Response.ContentType = contentType;
            Response.ContentLength = upstream.Content.Headers.ContentLength;
            Response.Headers.CacheControl = CacheControl;

            // ponytail: two requests racing the same cold id both fetch and the last Move wins. Thumbnails are
            // tens of kilobytes, so a duplicate download is cheaper than the coordination — add a keyed lock if
            // that ever stops being true.
            await PumpAsync(await upstream.Content.ReadAsStreamAsync(cancellationToken), Response.Body,
                Path.Combine(directory, $"{key}.{Guid.NewGuid():N}.part"),
                Path.Combine(directory, key + ExtensionFor(contentType)), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The client left mid-image; the body is already partly written, so there is nothing left to return.
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Fetching cover for '{ID}' failed", id);
            if (!Response.HasStarted)
                return NotFound(new ApiErrorBody(new ApiError("not_found", "The artwork could not be fetched.")));
        }

        return new EmptyResult();
    }

    /// <summary>
    ///     Copies <paramref name="source" /> into the response and the cache at the same time, so the client starts
    ///     receiving the image on the first chunk off the wire instead of after the whole download. Only a body that
    ///     was read to the end is published to <paramref name="cachePath" /> — a truncated image in the cache would
    ///     be served forever.
    /// </summary>
    public static async Task PumpAsync(Stream source, Stream destination, string temporaryPath, string cachePath,
        CancellationToken cancellationToken = default)
    {
        var buffer = new byte[16 * 1024];
        var complete = false;

        try
        {
            await using (var file = SFile.Create(temporaryPath))
            {
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    // Response first, and flushed: the point of the endpoint is that the image paints while it downloads.
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    await destination.FlushAsync(cancellationToken);
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }

                complete = true;
            }

            SFile.Move(temporaryPath, cachePath, true);
        }
        finally
        {
            if (!complete) SFile.Delete(temporaryPath);
        }
    }

    private static string ExtensionFor(string contentType)
    {
        return ImageTypes.FirstOrDefault(pair => pair.Value == contentType).Key ?? ".jpg";
    }

    private static string ContentTypeFor(string path)
    {
        return ImageTypes.GetValueOrDefault(Path.GetExtension(path), "image/jpeg");
    }
}