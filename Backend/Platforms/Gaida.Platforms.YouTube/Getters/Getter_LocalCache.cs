using Gaida.Core.Platforms;
using Gaida.Core.Streams;
using Serilog;

namespace Gaida.Platforms.YouTube.Getters;

public class GetterLocalCache(ILogger logger) : ContentGetter(logger)
{
    private string _cacheLocation = "./YouTube Cache";
    public override int Priority => 99;

    public override void Initialize()
    {
        var env = Environment.GetEnvironmentVariable("YOUTUBE_CACHE", EnvironmentVariableTarget.Process);

        if (env is null) Environment.SetEnvironmentVariable("YOUTUBE_CACHE", _cacheLocation);
        _cacheLocation = env ?? _cacheLocation;

        base.Initialize();
    }

    public override Task<StreamSpreader?> GetContentDataAsync(PlatformResult result,
        CancellationToken cancellationToken)
    {
        if (result is not YouTubeResult youtubeResult)
        {
            Logger.Debug("Result is not a YouTubeResult");
            return Task.FromResult<StreamSpreader?>(null);
        }

        var file = youtubeResult.GetPureId().ToString() + ".webm";
        Directory.CreateDirectory(_cacheLocation);

        var path = Path.Combine(_cacheLocation, file);
        if (!File.Exists(path))
        {
            Logger.Debug("Not in the local cache: {Path}", path);
            return Task.FromResult<StreamSpreader?>(null);
        }

        // Already on disk -- adopt the cached file rather than reading 90G of webm through the heap.
        return Task.FromResult<StreamSpreader?>(StreamSpreader.FromExistingFile(path));
    }
}
