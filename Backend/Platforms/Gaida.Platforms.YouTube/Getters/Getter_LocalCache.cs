using Gaida.Core.Platforms;
using Gaida.Core.Streams;
using Serilog;

namespace Gaida.Platforms.YouTube.Getters;

/// <param name="logger">Where the getter reports what it did.</param>
/// <param name="cacheLocation">The webm cache, <see cref="YouTubeOptions.AudioCacheDirectory" />.</param>
public class GetterLocalCache(ILogger logger, string cacheLocation) : ContentGetter(logger)
{
    public override int Priority => 99;

    public override Task<StreamSpreader?> GetContentDataAsync(PlatformResult result,
        CancellationToken cancellationToken)
    {
        if (result is not YouTubeResult youtubeResult)
        {
            Logger.Debug("Result is not a YouTubeResult");
            return Task.FromResult<StreamSpreader?>(null);
        }

        var file = youtubeResult.GetPureId().ToString() + ".webm";
        Directory.CreateDirectory(cacheLocation);

        var path = Path.Combine(cacheLocation, file);
        if (!File.Exists(path))
        {
            Logger.Debug("Not in the local cache: {Path}", path);
            return Task.FromResult<StreamSpreader?>(null);
        }

        // Already on disk -- adopt the cached file rather than reading 90G of webm through the heap.
        return Task.FromResult<StreamSpreader?>(StreamSpreader.FromExistingFile(path));
    }

    /// <summary>
    ///     Tells a freshly started download to land in the webm cache instead of scratch space.
    /// </summary>
    /// <remarks>
    ///     This used to subscribe to the spreader and write every chunk out a second time, into a second
    ///     file, behind its own queue and semaphore. Now that a spreader is already a file, keeping it is a
    ///     move at close -- the bytes are only ever written once. Readers streaming the download while it
    ///     happens are unaffected: they hold their own handles.
    /// </remarks>
    public void Keep(YouTubeResult result, StreamSpreader streamSpreader)
    {
        var filePath = Path.Combine(cacheLocation, $"{result.GetPureId()}.webm");

        // Already cached, or already being served straight out of the cache by GetContentDataAsync -- in which
        // case the spreader's file IS filePath and moving it onto itself would be nonsense.
        if (File.Exists(filePath)) return;

        streamSpreader.KeepAs(filePath);
    }
}
