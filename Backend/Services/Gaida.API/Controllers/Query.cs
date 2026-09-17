using System.Globalization;
using Gaida.API.Contracts;
using Gaida.Core;
using Gaida.Core.Platforms;
using Microsoft.AspNetCore.Mvc;

namespace Gaida.API.Controllers;

[ApiController]
[Route("[controller]")]
public class Query(ILogger<Query> logger, IConfiguration configuration, IHostEnvironment environment) : ControllerBase
{
    [HttpGet]
    [Route("/Audio/FindQueryType")]
    [Produces("application/json")]
    [ProducesResponseType<QueryResolutionDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorBody>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorBody>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<QueryResolutionDto>> FindQueryType(string? query,
        [FromServices] ManagerService managerService, [FromServices] PlayableResolver resolver)
    {
        if (string.IsNullOrWhiteSpace(query))
            return BadRequest(Error("invalid_query", "Query is required."));

        var trimmed = query.Trim();
        try
        {
            // Fanned out across every platform pod's /classify: first claim wins, and nobody claiming means
            // an ordinary keyword search — the one classification rule left in Gaida.
            var claim = await managerService.Manager.ClassifyAsync(trimmed, HttpContext.RequestAborted);
            if (claim.Error is not null) return BadRequest(Error("invalid_query", claim.Error));

            return claim.Kind switch
            {
                QueryType.Id => await ResolveOne(claim.Query, managerService, resolver),
                QueryType.Playlist => Ok(PlaylistResolution(claim.Query)),
                _ => Ok(new QueryResolutionDto { Kind = "search", Query = trimmed })
            };
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to resolve query {Query}", trimmed);
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                Error("resolver_unavailable", "The query resolver is temporarily unavailable."));
        }
    }

    /// <summary>
    ///     Searches the local library only: the client sends the title rather than an ID, so this touches
    ///     nothing but the local pod's in-memory song list and can run after every roll.
    /// </summary>
    [HttpGet]
    [Route("/Audio/Local/Variant")]
    [Produces("application/json")]
    [ProducesResponseType<LocalVariantDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ApiErrorBody>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<LocalVariantDto>> LocalVariant(string? name, string? artist, string? duration,
        [FromServices] ManagerService managerService)
    {
        if (string.IsNullOrWhiteSpace(name))
            return BadRequest(Error("invalid_query", "A track name is required."));

        var length = TimeSpan.Zero;
        if (!string.IsNullOrWhiteSpace(duration) &&
            !TimeSpan.TryParse(duration, CultureInfo.InvariantCulture, out length))
            return BadRequest(Error("invalid_query", "The duration must look like 00:04:32."));

        if (managerService.Manager.PlatformFor("audio://") is not HttpPlatform local) return NoContent();

        var found = await local.VariantAsync(name, artist, length, HttpContext.RequestAborted);
        if (found?.Result is null) return NoContent();

        var mapped = ToSearchResult(found.Result);
        if (mapped is null) return NoContent();

        return Ok(new LocalVariantDto(
            found.Match ?? "weak",
            Math.Round(found.Score, 3),
            found.DurationDeltaSeconds,
            found.YouTubeTags ?? [],
            found.LibraryTags ?? [],
            mapped));
    }

    private async Task<ActionResult<QueryResolutionDto>> ResolveOne(string id, ManagerService managerService,
        PlayableResolver resolver)
    {
        var result = await managerService.Manager.SearchId(id, HttpContext.RequestAborted);

        // A Spotify link resolves to a name; what the client gets back is whatever platform actually has the
        // track, which is also what `kind` then reports. A Deezer link normally passes straight through —
        // that pod has the audio — unless it is running without an ARL, when it resolves like Spotify does.
        if (result is not null) result = await resolver.ResolveOne(result, HttpContext.RequestAborted);
        if (result is null) return NotFound(Error("not_found", "No result was found for this ID."));

        var mapped = DiscoveryResultMapper.Map(result, Request, configuration, environment);
        return mapped is null
            ? NotFound(Error("not_found", "No result was found for this ID."))
            : Ok(new QueryResolutionDto { Kind = KindOf(result.Id), Query = id, Result = mapped });
    }

    /// <summary>
    ///     The playlist schemes whose ID is simply the rest of the string, and the <c>kind</c> each reports.
    ///     YouTube is deliberately absent: its normalized form is not the only shape a claim arrives in, so it
    ///     goes through <see cref="ExtractPlaylistId" /> instead.
    /// </summary>
    private static readonly (string Scheme, string Kind)[] PlaylistSchemes =
    [
        ("spotify-playlist://", "spotifyPlaylist"),
        ("deezer-playlist://", "deezerPlaylist")
    ];

    /// <summary>
    ///     A playlist claim resolves to its identity only — <c>kind</c>, the canonical <c>query</c> and
    ///     <c>playlistId</c> — all of which classify already produced. The entries are not looked up here:
    ///     they live inside an envelope, and a half-written envelope is not something a streaming client can
    ///     read. Send the canonical <c>query</c> to <c>/Audio/Search</c>, which routes a playlist claim to the
    ///     same lookup and streams the entries as they resolve — see API.md.
    /// </summary>
    private static QueryResolutionDto PlaylistResolution(string playlistUrl)
    {
        foreach (var (scheme, kind) in PlaylistSchemes)
            if (playlistUrl.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
                return new QueryResolutionDto
                {
                    Kind = kind,
                    Query = playlistUrl,
                    PlaylistId = playlistUrl[scheme.Length..]
                };

        return new QueryResolutionDto
        {
            Kind = "youtubePlaylist",
            Query = playlistUrl,
            PlaylistId = ExtractPlaylistId(playlistUrl)
        };
    }

    private SearchResultDto? ToSearchResult(PodResultDto dto)
    {
        // The local pod's own conversion lives on HttpPlatform; this is the one-off case where a
        // sub-resource (the variant's matched track) needs the same treatment without a full search.
        return DiscoveryResultMapper.Map(new HttpResult
        {
            Id = dto.Id ?? "",
            Name = dto.Name,
            Artist = dto.Artist,
            Album = dto.Album,
            Duration = TimeSpan.TryParse(dto.Duration, CultureInfo.InvariantCulture, out var d) ? d : TimeSpan.Zero,
            ThumbnailUrl = dto.ThumbnailUrl,
            OriginalTitle = dto.OriginalTitle,
            OriginalArtist = dto.OriginalArtist,
            Downloaders = []
        }, Request, configuration, environment);
    }

    /// <summary>
    ///     The response's <c>kind</c> for a resolved ID, read off its protocol prefix — display metadata,
    ///     not routing (routing already happened via <c>/classify</c>).
    /// </summary>
    private static string KindOf(string id)
    {
        return id switch
        {
            _ when id.StartsWith("audio://", StringComparison.Ordinal) => "local",
            _ when id.StartsWith("yt://", StringComparison.Ordinal) => "youtubeVideo",
            _ when id.StartsWith("spotify://", StringComparison.Ordinal) => "spotifyTrack",
            _ when id.StartsWith("deezer://", StringComparison.Ordinal) => "deezerTrack",
            _ => "id"
        };
    }

    /// <summary>
    ///     Pulls a YouTube-style <c>list=</c> parameter off a normalized playlist URL, for the
    ///     <c>playlistId</c> field the public contract documents. The pod normalizes to
    ///     <c>yt-playlist://PL…</c>, which carries no <c>list=</c> — there the id is the whole tail, and
    ///     without this the documented field came back missing on every YouTube playlist.
    /// </summary>
    private static string? ExtractPlaylistId(string url)
    {
        const string scheme = "yt-playlist://";
        if (url.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return url[scheme.Length..];

        var index = url.IndexOf("list=", StringComparison.Ordinal);
        if (index < 0) return null;

        var start = index + "list=".Length;
        var end = url.IndexOfAny(['&', '#'], start);
        var value = end < 0 ? url[start..] : url[start..end];
        return Uri.UnescapeDataString(value);
    }

    private static ApiErrorBody Error(string code, string message)
    {
        return new ApiErrorBody(new ApiError(code, message));
    }
}