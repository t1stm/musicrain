using System.Runtime.CompilerServices;
using Gaida.API.Contracts;
using Gaida.Core.Platforms;
using Gaida.Infrastructure.Platforms;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Gaida.API.Controllers;

[ApiController]
[Route("[controller]")]
public class Album(IOptionsSnapshot<GaidaApiOptions> options, IHostEnvironment environment) : ControllerBase
{
    /// <summary>
    ///     Streams one album's tracks. The library is asked first and Deezer only if it had none, so a record
    ///     someone owns plays from the library rather than over the network.
    /// </summary>
    /// <remarks>
    ///     One endpoint rather than the Local/YouTube pair Artist has: there is exactly one album by that name
    ///     and that artist, and the only question is who has it — a client choosing a side would be choosing
    ///     between two answers to the same question. Ordering is each pod's own, which for an album is the
    ///     running order; sorting here would mean holding the whole response first.
    /// </remarks>
    [HttpGet]
    [Route("/Audio/Album")]
    [Produces("application/json")]
    [ProducesResponseType<IReadOnlyList<SearchResultDto>>(StatusCodes.Status200OK)]
    public IActionResult GetAlbum(string? artist, string? album, [FromServices] ManagerService managerService)
    {
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(album))
            return Ok(Array.Empty<SearchResultDto>());

        return Ok(this.Mapped(FirstNonEmpty(artist, album, managerService, HttpContext.RequestAborted),
            options.Value, environment));
    }

    /// <summary>
    ///     Each pod that lists albums, in preference order, stopping at the first that has one. "Has one" can
    ///     only be known by asking, so this yields the first pod's tracks as they arrive and moves on only
    ///     when that pod turned out to have none — no buffering, and no second call once one has answered.
    /// </summary>
    /// <remarks>
    ///     The library holds an album only when a playlist file defines it, so a tagged album nobody
    ///     assembled is the ordinary case for reaching Deezer, not an edge one. A deployment without a Deezer
    ///     pod loses the fallback and nothing else.
    /// </remarks>
    private static async IAsyncEnumerable<PlatformResult> FirstNonEmpty(string artist, string album,
        ManagerService managerService, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var prefix in (string[])[Prefix.Local, Prefix.Deezer])
        {
            if (managerService.Pod(prefix) is not { } pod) continue;

            var any = false;
            await foreach (var result in pod.AlbumAsync(artist, album, cancellationToken))
            {
                any = true;
                yield return result;
            }

            if (any) yield break;
        }
    }
}
