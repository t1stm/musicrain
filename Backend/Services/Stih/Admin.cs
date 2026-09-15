using Gaida.Admin;

namespace Stih;

/// <summary>
///     Stih's admin surface: one snapshot, no actions. Everything this service does to the world is
///     driven by listeners and by the sweep, and there is nothing an operator would want to push.
/// </summary>
internal static class Admin
{
    public static void MapStihAdmin(this WebApplication app)
    {
        var index = app.Services.GetRequiredService<LyricsIndex>();
        var lyrics = app.Services.GetRequiredService<Lyrics>();
        var lrcLib = app.Services.GetRequiredService<LrcLib>();
        var tracks = app.Services.GetRequiredService<Tracks>();
        var sweep = app.Services.GetServices<IHostedService>().OfType<Sweep>().FirstOrDefault();

        app.MapAdmin(object? () => new
        {
            service = "stih",
            index = index.Snapshot(),
            files = lyrics.Snapshot(),
            lrclib = lrcLib.Snapshot(),
            sweep = sweep?.Snapshot(),
            pods = new { lastError = tracks.LastError }
        });
    }
}
