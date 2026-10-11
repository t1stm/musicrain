using Gaida.API.Contracts;
using Gaida.Core.Utils;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Gaida.API.Controllers;

[ApiController]
[Route("[controller]")]
public class Browse(IOptionsSnapshot<GaidaApiOptions> options, IHostEnvironment environment) : ControllerBase
{
    /// <summary>
    ///     One level of the library's folder tree. A path nobody has is an empty folder rather than a 404:
    ///     the local pod's tree lookup never touches the filesystem, so "unknown" and "empty" are the same
    ///     answer and the client renders both the same way.
    /// </summary>
    [HttpGet]
    [Route("/Audio/Browse")]
    [Produces("application/json")]
    [ProducesResponseType<BrowseDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<BrowseDto>> GetLevel(string? path, [FromServices] ManagerService managerService)
    {
        // Normalized once here so the echoed path and the folder paths built below agree with each other
        // whatever the client sent.
        var folder = FolderPath.Normalize(path);

        if (managerService.Pod(Prefix.Local) is not { } local)
            return Ok(new BrowseDto(folder, [], []));

        var (folders, files) = await local.BrowseAsync(folder, HttpContext.RequestAborted);

        var mapped = new List<SearchResultDto>(files.Count);
        mapped.AddRange(files.Select(file => DiscoveryResultMapper.Map(file, Request, options.Value, environment))
            .OfType<SearchResultDto>());

        return Ok(new BrowseDto(
            folder,
            [
                .. folders.Select(child => new BrowseFolderDto(child.Name,
                    folder.Length == 0 ? child.Name : $"{folder}/{child.Name}", child.Songs))
            ],
            mapped));
    }

    /// <summary>
    ///     Every track beneath a folder, for its Play All: subfolders first and then files at each level, the
    ///     order the explorer lists them with every folder opened. Streamed like an album, and the same
    ///     empty answer as above for a path nobody has.
    /// </summary>
    [HttpGet]
    [Route("/Audio/Browse/Tracks")]
    [Produces("application/json")]
    [ProducesResponseType<IReadOnlyList<SearchResultDto>>(StatusCodes.Status200OK)]
    public IActionResult GetTracks(string? path, [FromServices] ManagerService managerService)
    {
        var folder = FolderPath.Normalize(path);

        if (managerService.Pod(Prefix.Local) is not { } local)
            return Ok(Array.Empty<SearchResultDto>());

        return Ok(this.Mapped(local.BrowseTracksAsync(folder, HttpContext.RequestAborted), options.Value,
            environment));
    }
}