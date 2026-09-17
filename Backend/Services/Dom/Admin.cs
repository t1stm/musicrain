using Dom.Store;
using Gaida.Admin;

namespace Dom;

/// <summary>
///     Dom's half of the admin surface. The only one in the stack whose actions destroy data that no
///     cache refills, so every route here is a deliberate, named operation rather than a general
///     "edit the store" endpoint — see ADMIN_PLAN.md.
/// </summary>
internal static class Admin
{
    public static void MapDomAdmin(this WebApplication app)
    {
        var store = app.Services.GetRequiredService<DomStore>();
        var covers = app.Configuration["Dom:CoverDir"] ?? "covers";

        var admin = app.MapAdmin(store.Snapshot);
        if (admin is null) return;

        admin.MapPost("/rename-user", (string username, string name) => Answer(store.AdminRenameUser(username, name)));

        // The new password arrives in the query string like every other parameter here. That is fine
        // on this surface and only here: the channel is Oko over the Docker network, the request never
        // touches a browser address bar, and Oko redacts the value before it reaches the audit log.
        admin.MapPost("/reset-password", (string username, string password) =>
            Answer(store.AdminSetPassword(username, password)));

        admin.MapPost("/sign-out-user", (string username) =>
        {
            var (ok, revoked) = store.AdminSignOut(username);
            return ok ? Results.Ok(new { revoked }) : Results.NotFound();
        });

        admin.MapPost("/delete-user", (string username) =>
        {
            var (ok, orphaned, deleted) = store.AdminDeleteUser(username);
            if (!ok) return Results.NotFound();

            foreach (var cover in orphaned) Forget(covers, cover);
            return Results.Ok(new { deletedPlaylists = deleted });
        });

        admin.MapPost("/rename-playlist", (string id, string name) =>
            Answer(store.AdminUpdatePlaylist(id, name, null, null)));

        admin.MapPost("/set-playlist-public", (string id, bool isPublic) =>
            Answer(store.AdminUpdatePlaylist(id, null, isPublic, null)));

        admin.MapPost("/remove-track", (string id, int index) =>
            Answer(store.AdminUpdatePlaylist(id, null, null, index)));

        admin.MapPost("/delete-playlist", (string id) =>
        {
            var (ok, cover) = store.AdminDeletePlaylist(id);
            if (!ok) return Results.NotFound();

            if (cover is not null) Forget(covers, cover);
            return Results.Ok(new { deleted = id });
        });
    }

    /// <summary>400 with the reason, rather than a bare failure: the operator needs to know why.</summary>
    private static IResult Answer((bool ok, string? error) result) =>
        result.ok ? Results.Ok() : Results.BadRequest(new { result.error });

    /// <summary>Same best-effort unlink the playlist controller does, for the same reason.</summary>
    private static void Forget(string directory, string coverFile)
    {
        try
        {
            File.Delete(Path.Combine(directory, coverFile));
        }
        catch (IOException)
        {
            // ponytail: a leftover cover costs a few kilobytes; failing the delete would cost the operator
        }
    }
}
