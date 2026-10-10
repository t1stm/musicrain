using Dom.Store;
using Microsoft.AspNetCore.Mvc;

namespace Dom.Controllers;

/// <summary>
///     Playlists: everybody's public ones, your friends', your own, and the CRUD behind them.
/// </summary>
/// <remarks>
///     A playlist you may not see answers <c>404</c>, never <c>403</c> — a 403 confirms it exists.
///     One you can see but may not change answers <c>403</c>: it is no secret that it exists.
/// </remarks>
public class Playlists(DomStore store, CoverFiles covers) : ControllerBase
{
    /// <summary>What a cover may be. Anything else is a file the browser would not draw anyway.</summary>
    private static readonly Dictionary<string, string> CoverTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/png"] = "png",
        ["image/jpeg"] = "jpg",
        ["image/webp"] = "webp"
    };

    /// <summary>2 MB. nginx already allows ten, so this is the rule, not the limit it hits first.</summary>
    private const long MaxCoverBytes = 2 * 1024 * 1024;

    [HttpGet("/Audio/Playlists/Public")]
    public IActionResult PublicPlaylists() => new JsonResult(store.Public().Select(Summary));

    /// <summary>Friends-only playlists of the caller's friends. Their public ones are under <c>/Public</c>.</summary>
    [HttpGet("/Audio/Playlists/Friends")]
    public IActionResult FriendsPlaylists()
    {
        return store.Caller(Request) is { } user
            ? new JsonResult(store.FriendsPlaylists(user).Select(Summary))
            : Api.SignInFirst();
    }

    /// <summary>Everything the caller can edit: what they own, and what friends let them change.</summary>
    [HttpGet("/Audio/Playlists/Mine")]
    public IActionResult Mine()
    {
        return store.Caller(Request) is { } user
            ? new JsonResult(store.Mine(user).Select(Summary))
            : Api.SignInFirst();
    }

    /// <summary>A bearer token is optional here: a public playlist is a link you can send anyone.</summary>
    [HttpGet("/Audio/Playlists/{id}")]
    public IActionResult One(string id)
    {
        var playlist = store.Visible(id, store.Caller(Request));

        return playlist is null ? NotFound() : new JsonResult(Full(playlist));
    }

    [HttpPost("/Audio/Playlists")]
    public IActionResult Create([FromBody] PlaylistBody? body)
    {
        if (store.Caller(Request) is not { } user) return Api.SignInFirst();
        if (body is null) return Api.Invalid("Send a name.");

        var outcome = store.Create(user, body.Name, body.Visibility ?? Visibility.Private, body.Tracks);

        return outcome.Ok ? new JsonResult(Full(outcome.Value)) { StatusCode = 201 } : Api.Refuse(outcome.Refusal);
    }

    [HttpPatch("/Audio/Playlists/{id}")]
    public IActionResult Patch(string id, [FromBody] PlaylistBody? body)
    {
        if (store.Caller(Request) is not { } user) return Api.SignInFirst();
        if (body is null) return Api.Invalid("Send something to change.");

        var outcome = store.Update(user, id, body.Name, body.Visibility, body.Tracks, body.Collaborators,
            body.Revision);

        return outcome.Ok ? new JsonResult(Full(outcome.Value)) : Api.Refuse(outcome.Refusal);
    }

    /// <summary>
    ///     One track onto the end, for a client that has a track and not the list — a PATCH would
    ///     have it read the list first and send the whole of it back.
    /// </summary>
    [HttpPost("/Audio/Playlists/{id}/Tracks")]
    public IActionResult Append(string id, [FromBody] TrackSnapshot? track)
    {
        if (store.Caller(Request) is not { } user) return Api.SignInFirst();

        var outcome = store.Append(user, id, track);

        return outcome.Ok
            ? new JsonResult(new { added = outcome.Value.Added, playlist = Summary(outcome.Value.Playlist) })
            : Api.Refuse(outcome.Refusal);
    }

    [HttpDelete("/Audio/Playlists/{id}")]
    public IActionResult Delete(string id)
    {
        if (store.Caller(Request) is not { } user) return Api.SignInFirst();

        var (deleted, coverFile) = store.Delete(user, id);
        if (coverFile is not null) covers.Forget(coverFile);

        return deleted ? NoContent() : NotFound();
    }

    /// <summary>
    ///     Replaces the playlist's cover. One file per playlist — the old one is deleted, so a
    ///     library of abandoned covers cannot build up behind a playlist that only has one.
    /// </summary>
    [HttpPut("/Audio/Playlists/{id}/Cover")]
    public async Task<IActionResult> UploadCover(string id)
    {
        if (store.Caller(Request) is not { } user) return Api.SignInFirst();
        if (!Request.HasFormContentType || Request.Form.Files.Count == 0)
            return Api.Invalid("Send an image as multipart form data.");

        var file = Request.Form.Files[0];
        if (!CoverTypes.TryGetValue(file.ContentType, out var extension))
            return Api.Invalid("A cover is a PNG, a JPEG or a WebP.");
        if (file.Length > MaxCoverBytes)
            return Api.Invalid("A cover is at most 2 MB.");

        // ownership decides before a byte is written; `id` never reaches the filesystem unchecked
        var playlist = store.Visible(id, user);
        if (playlist is null || playlist.Owner != user.Username) return NotFound();

        var name = await covers.SaveAsync(playlist.Id, extension, file, HttpContext.RequestAborted);

        if (playlist.CoverFile is not null && playlist.CoverFile != name) covers.Forget(playlist.CoverFile);
        store.SetCover(user, playlist.Id, name);

        return new JsonResult(new { coverUrl = $"/Audio/Playlists/{playlist.Id}/Cover" });
    }

    /// <summary>
    ///     The cover itself, from Dom's own origin — inside the Discord activity the API is the only
    ///     image host that is mapped, which is why this is not a redirect somewhere else.
    /// </summary>
    [HttpGet("/Audio/Playlists/{id}/Cover")]
    public IActionResult Cover(string id)
    {
        // a private playlist's cover is as private as the playlist
        var playlist = store.Visible(id, store.Caller(Request));
        if (playlist?.CoverFile is null) return NotFound();

        if (covers.Find(playlist.CoverFile) is not { } path) return NotFound();

        Response.Headers.CacheControl = "public, max-age=604800";

        return PhysicalFile(path, MimeFor(playlist.CoverFile));
    }

    private static string MimeFor(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        _ => "image/jpeg"
    };

    /// <summary>What a card needs, and nothing more.</summary>
    private static object Summary(Playlist playlist) => new
    {
        id = playlist.Id,
        name = playlist.Name,
        owner = playlist.Owner,
        visibility = playlist.Visibility,
        collaborators = playlist.Collaborators,
        trackCount = playlist.Tracks.Count,
        duration = playlist.Duration.ToString("c"),
        // A path, not an absolute URL: inside the Discord activity the API is reachable only under
        // the frame's own /.proxy prefix, so the client is the one that knows its own base.
        coverUrl = CoverUrl(playlist),
        firstTrackId = playlist.Tracks.FirstOrDefault()?.Id,
        firstTrackThumbnailUrl = playlist.Tracks.FirstOrDefault()?.ThumbnailUrl,
        createdUtc = playlist.CreatedUtc,
        updatedUtc = playlist.UpdatedUtc
    };

    private static string? CoverUrl(Playlist playlist) =>
        playlist.CoverFile is null ? null : $"/Audio/Playlists/{playlist.Id}/Cover";

    /// <summary>The summary plus the tracks themselves.</summary>
    private static object Full(Playlist playlist) => new
    {
        id = playlist.Id,
        name = playlist.Name,
        owner = playlist.Owner,
        visibility = playlist.Visibility,
        collaborators = playlist.Collaborators,
        revision = playlist.Revision,
        trackCount = playlist.Tracks.Count,
        duration = playlist.Duration.ToString("c"),
        coverUrl = CoverUrl(playlist),
        firstTrackId = playlist.Tracks.FirstOrDefault()?.Id,
        firstTrackThumbnailUrl = playlist.Tracks.FirstOrDefault()?.ThumbnailUrl,
        createdUtc = playlist.CreatedUtc,
        updatedUtc = playlist.UpdatedUtc,
        tracks = playlist.Tracks
    };

    /// <summary>
    ///     Create and patch share a body. On a patch a missing field means "leave it alone", which is
    ///     why every field is nullable rather than defaulted. <c>Revision</c> goes with <c>Tracks</c>:
    ///     it is the list the caller started from.
    /// </summary>
    public sealed record PlaylistBody(
        string? Name, Visibility? Visibility, List<TrackSnapshot>? Tracks, List<string>? Collaborators, int? Revision);
}
