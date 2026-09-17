using Gaida.Core.Platforms;
using Gaida.Core.Platforms.Optional.Supports;
using Gaida.Platforms.YouTube.Cache;
using Serilog;

namespace Gaida.Platforms.YouTube.Search_Providers;

public class YouTubeSearchProviderCached(ILogger logger, YouTubeCacher cacher) : SearchProvider(logger), ISupportsId
{
    public override string PlatformIdentifier => "yt://";
    public override int Priority => 99;

    public async Task<PlatformResult?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        var result = await cacher.GetFromCacheAsync(id);
        if (result is null)
        {
            Logger.Debug("No cached YouTube result for ID: {ID}", id);
            return null;
        }

        result.Downloaders = ContentDownloaders;
        return result;
    }

    protected override void Initialize()
    {
        cacher.InitializeAsync().GetAwaiter().GetResult();
    }
}
