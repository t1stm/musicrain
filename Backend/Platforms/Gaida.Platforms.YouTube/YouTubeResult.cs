using Gaida.Core.Platforms;
using Gaida.Core.Platforms.Optional.Supports;
using Gaida.Core.Streams;

namespace Gaida.Platforms.YouTube;

public sealed class YouTubeResult : PlatformResult, ISupportsCaching
{
    public Task RunCacheProcess(StreamSpreader streamSpreader)
    {
        return Task.Run(async () => { await YouTubeCacheProvider.UpdateCache(this, streamSpreader); });
    }

    public override string GetDownloadUrl()
    {
        return $"https://www.youtube.com/watch?v={GetPureId()}";
    }
}