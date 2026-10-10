using Gaida.API.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Gaida.API.Controllers;

/// <summary>
///     Serves track artwork from the API's own origin. An embedded Discord activity can only reach the hosts it has
///     URL mappings for, and the thumbnails the discovery endpoints hand out live on ytimg and the cover host — so
///     the API fetches them once, keeps them on disk, and hands them back under the `/api` mapping the activity has.
/// </summary>
[ApiController]
[Route("[controller]")]
public class Cover(ILogger<Cover> logger, ThumbnailCache thumbnails) : ControllerBase
{
    /// <summary>Artwork is small and immutable in practice, but a re-extracted local cover should still land within a week.</summary>
    private const string CacheControl = "public, max-age=604800";

    [HttpGet]
    [Route("/Audio/Cover")]
    // The image types belong to the 200 alone. As [Produces] they filtered every result, and the JSON error
    // bodies below had no formatter left that would write them as an image -- every 400 and 404 went out as 406.
    [ProducesResponseType(typeof(Stream), StatusCodes.Status200OK, "image/jpeg", "image/png", "image/webp", "image/gif")]
    [ProducesResponseType<ApiErrorBody>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorBody>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string? id, [FromServices] ManagerService managerService)
    {
        if (string.IsNullOrWhiteSpace(id))
            return BadRequest(new ApiErrorBody(new ApiError("invalid_query", "An id is required.")));

        // A hit never touches the platform layer, which is what makes the cached path fast.
        if (thumbnails.Find(id) is { } cached)
        {
            Response.Headers.CacheControl = CacheControl;
            return PhysicalFile(cached, ThumbnailCache.ContentTypeFor(cached));
        }

        var cancellationToken = HttpContext.RequestAborted;
        var result = await managerService.Manager.SearchId(id, cancellationToken);
        if (result?.ThumbnailUrl is not { Length: > 0 } source)
            return NotFound(new ApiErrorBody(new ApiError("not_found", "This id has no artwork.")));

        logger.LogInformation("Caching cover for '{ID}' from {Source}", id, source);

        try
        {
            using var upstream = await thumbnails.FetchAsync(source, cancellationToken);
            if (!upstream.IsSuccessStatusCode)
                return NotFound(new ApiErrorBody(new ApiError("not_found", "The artwork could not be fetched.")));

            var contentType = upstream.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
            Response.ContentType = contentType;
            Response.ContentLength = upstream.Content.Headers.ContentLength;
            Response.Headers.CacheControl = CacheControl;

            await thumbnails.KeepWhileServingAsync(id, await upstream.Content.ReadAsStreamAsync(cancellationToken),
                contentType, Response.Body, cancellationToken);
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
}
