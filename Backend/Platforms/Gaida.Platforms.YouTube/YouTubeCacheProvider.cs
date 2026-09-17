using Gaida.Core.Platforms;
using Gaida.Core.Streams;

namespace Gaida.Platforms.YouTube;

public static class YouTubeCacheProvider
{
    /// <summary>
    ///     Tells a freshly started download to land in the webm cache instead of scratch space.
    /// </summary>
    /// <remarks>
    ///     This used to subscribe to the spreader and write every chunk out a second time, into a second
    ///     file, behind its own queue and semaphore. Now that a spreader is already a file, keeping it is a
    ///     move at close -- the bytes are only ever written once. Readers streaming the download while it
    ///     happens are unaffected: they hold their own handles.
    /// </remarks>
    public static Task UpdateCache(PlatformResult result, StreamSpreader streamSpreader)
    {
        if (result is not YouTubeResult youtubeResult) return Task.CompletedTask;

        var exportDirectory = Environment.GetEnvironmentVariable("YOUTUBE_CACHE", EnvironmentVariableTarget.Process);
        if (exportDirectory is null) return Task.CompletedTask;

        var filePath = Path.Combine(exportDirectory, $"{youtubeResult.GetPureId()}.webm");

        // Already cached, or already being served straight out of the cache by GetterLocalCache -- in which
        // case the spreader's file IS filePath and moving it onto itself would be nonsense.
        if (File.Exists(filePath)) return Task.CompletedTask;

        streamSpreader.KeepAs(filePath);
        return Task.CompletedTask;
    }
}
