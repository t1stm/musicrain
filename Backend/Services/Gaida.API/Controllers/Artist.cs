using Gaida.API.Contracts;
using Gaida.Core.Platforms;
using Microsoft.AspNetCore.Mvc;

namespace Gaida.API.Controllers;

[ApiController]
[Route("[controller]")]
public class Artist(IConfiguration configuration, IHostEnvironment environment) : ControllerBase
{
    /// <summary>
    ///     Streams the library's tracks for an artist. The artist/name/id ordering API.md documents is applied
    ///     by the local pod, which holds the whole library in memory — sorting here would mean holding the
    ///     whole response first, which is the one thing this endpoint is trying not to do.
    /// </summary>
    [HttpGet]
    [Route("/Audio/Artist/Local")]
    [Produces("application/json")]
    [ProducesResponseType<IReadOnlyList<SearchResultDto>>(StatusCodes.Status200OK)]
    public IActionResult GetArtistLocal(string? term, [FromServices] ManagerService managerService)
    {
        if (string.IsNullOrWhiteSpace(term) || managerService.Manager.PlatformFor("audio://") is not HttpPlatform local)
            return Ok(Array.Empty<SearchResultDto>());

        return Ok(this.Mapped(local.ArtistAsync(term, HttpContext.RequestAborted), configuration, environment));
    }

    /// <summary>
    ///     The artist's top tracks on Deezer, best-known first — see API.md. Empty when Deezer is metadata-only
    ///     here: its IDs would not play, and resolving each one elsewhere would just be the YouTube tab again.
    /// </summary>
    [HttpGet]
    [Route("/Audio/Artist/Deezer")]
    [Produces("application/json")]
    [ProducesResponseType<IReadOnlyList<SearchResultDto>>(StatusCodes.Status200OK)]
    public IActionResult GetArtistDeezer(string? term, [FromServices] ManagerService managerService)
    {
        if (string.IsNullOrWhiteSpace(term) || managerService.NeedsResolving("deezer://") ||
            managerService.Manager.PlatformFor("deezer://") is not HttpPlatform deezer)
            return Ok(Array.Empty<SearchResultDto>());

        return Ok(this.Mapped(deezer.ArtistAsync(term, HttpContext.RequestAborted), configuration, environment));
    }

    /// <summary>Relevance order, as YouTube returned it — see API.md.</summary>
    [HttpGet]
    [Route("/Audio/Artist/YouTube")]
    [Produces("application/json")]
    [ProducesResponseType<IReadOnlyList<SearchResultDto>>(StatusCodes.Status200OK)]
    public IActionResult GetArtistYouTube(string? term, [FromServices] ManagerService managerService)
    {
        if (string.IsNullOrWhiteSpace(term) || managerService.Manager.PlatformFor("yt://") is not HttpPlatform youTube)
            return Ok(Array.Empty<SearchResultDto>());

        // Just a keyword search on the YouTube pod: there is no separate "artist" route for it.
        return Ok(this.Mapped(youTube.SearchKeywords(term, HttpContext.RequestAborted), configuration, environment));
    }
}
