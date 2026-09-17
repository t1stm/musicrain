using Gaida.Admin;

namespace Dunav;

/// <summary>
///     Dunav's half of the admin surface: what is in the cache, and the two ways to take it out again.
///     The shared half — the token check, the request ring, the live feed — comes from
///     <see cref="AdminApi.MapAdmin(WebApplication,Func{object})" />.
/// </summary>
internal static class Admin
{
    public static void MapDunavAdmin(this WebApplication app)
    {
        var cache = app.Services.GetRequiredService<CacheService>();

        // null when ADMIN_TOKEN is unset: no secret, no admin surface, not even these routes.
        var admin = app.MapAdmin(cache.Snapshot);
        if (admin is null) return;

        // Unlinks the body and drops the key. Readers already streaming it finish off their own handle —
        // eviction is the same operation the sweep timer performs, not a new hazard.
        admin.MapPost("/evict", (string key) =>
            cache.Evict(key, "admin request") ? Results.Ok(new { evicted = key }) : Results.NotFound());

        admin.MapPost("/evict-all", () => Results.Ok(new { evicted = cache.EvictAll() }));
    }
}
