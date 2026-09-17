using Dom.Store;
using Microsoft.AspNetCore.Mvc;

namespace Dom.Controllers;

/// <summary>
///     Playlists: everybody's public ones, your own, and the CRUD behind them.
/// </summary>
/// <remarks>
///     A playlist you may not see answers <c>404</c>, never <c>403</c> — a 403 confirms it exists.
/// </remarks>
public class Playlists(DomStore store, IConfiguration config) : ControllerBase
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

    private string CoverDir => config["Dom:CoverDir"] ?? "covers";

    [HttpGet("/Audio/Playlists/Public")]
    public IActionResult PublicPlaylists() => new JsonResult(store.Public().Select(Summary));

    [HttpGet("/Audio/Playlists/Mine")]
    public IActionResult Mine()
    {
        var user = store.Resolve(Api.Bearer(Request));

        return user is null
            ? Api.Error(401, "unauthorized", "Sign in first.")
            : new JsonResult(store.Mine(user).Select(Summary));
    }

    /// <summary>A bearer token is optional here: a public playlist is a link you can send anyone.</summary>
    [HttpGet("/Audio/Playlists/{id}")]
    public IActionResult One(string id)
    {
        var playlist = store.Visible(id, store.Resolve(Api.Bearer(Request)));

        return playlist is null ? NotFound() : new JsonResult(Full(playlist));
    }

    [HttpPost("/Audio/Playlists")]
    public IActionResult Create([FromBody] PlaylistBody? body)
    {
        var user = store.Resolve(Api.Bearer(Request));
        if (user is null) return Api.Error(401, "unauthorized", "Sign in first.");
        if (body is null) return Api.Error(400, "invalid_request", "Send a name.");

        var (playlist, error, message) = store.Create(user, body.Name, body.IsPublic ?? false, body.Tracks);

        return error is not null
            ? Api.Error(400, error, message!)
            : new JsonResult(Full(playlist!)) { StatusCode = 201 };
    }

    [HttpPatch("/Audio/Playlists/{id}")]
    public IActionResult Patch(string id, [FromBody] PlaylistBody? body)
    {
        var user = store.Resolve(Api.Bearer(Request));
        if (user is null) return Api.Error(401, "unauthorized", "Sign in first.");
        if (body is null) return Api.Error(400, "invalid_request", "Send something to change.");

        var (playlist, error, message) = store.Update(user, id, body.Name, body.IsPublic, body.Tracks);

        return error switch
        {
            null => new JsonResult(Full(playlist!)),
            "not_found" => NotFound(),
            _ => Api.Error(400, error, message!)
        };
    }

    [HttpDelete("/Audio/Playlists/{id}")]
    public IActionResult Delete(string id)
    {
        var user = store.Resolve(Api.Bearer(Request));
        if (user is null) return Api.Error(401, "unauthorized", "Sign in first.");

        var (deleted, coverFile) = store.Delete(user, id);
        if (coverFile is not null) Forget(coverFile);

        return deleted ? NoContent() : NotFound();
    }

    /// <summary>
    ///     Replaces the playlist's cover. One file per playlist — the old one is deleted, so a
    ///     library of abandoned covers cannot build up behind a playlist that only has one.
    /// </summary>
    [HttpPut("/Audio/Playlists/{id}/Cover")]
    public async Task<IActionResult> UploadCover(string id)
    {
        var user = store.Resolve(Api.Bearer(Request));
        if (user is null) return Api.Error(401, "unauthorized", "Sign in first.");
        if (!Request.HasFormContentType || Request.Form.Files.Count == 0)
            return Api.Error(400, "invalid_request", "Send an image as multipart form data.");

        var file = Request.Form.Files[0];
        if (!CoverTypes.TryGetValue(file.ContentType ?? "", out var extension))
            return Api.Error(400, "invalid_request", "A cover is a PNG, a JPEG or a WebP.");
        if (file.Length > MaxCoverBytes)
            return Api.Error(400, "invalid_request", "A cover is at most 2 MB.");

        // ownership decides before a byte is written; `id` never reaches the filesystem unchecked
        var playlist = store.Visible(id, user);
        if (playlist is null || playlist.Owner != user.Username) return NotFound();

        Directory.CreateDirectory(CoverDir);
        var name = $"{playlist.Id}.{extension}";
        await using (var target = System.IO.File.Create(Path.Combine(CoverDir, name)))
        {
            await file.CopyToAsync(target);
        }

        if (playlist.CoverFile is not null && playlist.CoverFile != name) Forget(playlist.CoverFile);
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
        var playlist = store.Visible(id, store.Resolve(Api.Bearer(Request)));
        if (playlist?.CoverFile is null) return NotFound();

        var path = Path.Combine(CoverDir, playlist.CoverFile);
        if (!System.IO.File.Exists(path)) return NotFound();

        Response.Headers.CacheControl = "public, max-age=604800";

        return PhysicalFile(Path.GetFullPath(path), MimeFor(playlist.CoverFile));
    }

    private static string MimeFor(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        _ => "image/jpeg"
    };

    /// <summary>A cover nothing points at any more. A failure here is a stray file, never an error.</summary>
    private void Forget(string coverFile)
    {
        try
        {
            System.IO.File.Delete(Path.Combine(CoverDir, coverFile));
        }
        catch (IOException)
        {
            // ponytail: a leftover cover costs a few kilobytes; failing the delete would cost the user
        }
    }

    /// <summary>What a card needs, and nothing more.</summary>
    private static object Summary(Playlist playlist) => new
    {
        id = playlist.Id,
        name = playlist.Name,
        owner = playlist.Owner,
        isPublic = playlist.IsPublic,
        trackCount = playlist.Tracks.Count,
        duration = playlist.Duration.ToString("c"),
        // A path, not an absolute URL: inside the Discord activity the API is reachable only under
        // the frame's own /.proxy prefix, so the client is the one that knows its own base.
        coverUrl = playlist.CoverFile is null ? null : $"/Audio/Playlists/{playlist.Id}/Cover",
        firstTrackId = playlist.Tracks.FirstOrDefault()?.Id,
        firstTrackThumbnailUrl = playlist.Tracks.FirstOrDefault()?.ThumbnailUrl,
        createdUtc = playlist.CreatedUtc,
        updatedUtc = playlist.UpdatedUtc
    };

    /// <summary>The summary plus the tracks themselves.</summary>
    private static object Full(Playlist playlist) => new
    {
        id = playlist.Id,
        name = playlist.Name,
        owner = playlist.Owner,
        isPublic = playlist.IsPublic,
        trackCount = playlist.Tracks.Count,
        duration = playlist.Duration.ToString("c"),
        coverUrl = playlist.CoverFile is null ? null : $"/Audio/Playlists/{playlist.Id}/Cover",
        firstTrackId = playlist.Tracks.FirstOrDefault()?.Id,
        firstTrackThumbnailUrl = playlist.Tracks.FirstOrDefault()?.ThumbnailUrl,
        createdUtc = playlist.CreatedUtc,
        updatedUtc = playlist.UpdatedUtc,
        tracks = playlist.Tracks
    };

    /// <summary>
    ///     Create and patch share a body. On a patch a missing field means "leave it alone", which is
    ///     why every field is nullable rather than defaulted.
    /// </summary>
    public sealed record PlaylistBody(string? Name, bool? IsPublic, List<TrackSnapshot>? Tracks);
}
