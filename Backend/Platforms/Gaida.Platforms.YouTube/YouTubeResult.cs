using Gaida.Core.Platforms;
using Gaida.Core.Platforms.Optional.Supports;
using Gaida.Core.Streams;
using Gaida.Platforms.YouTube.Getters;

namespace Gaida.Platforms.YouTube;

public sealed class YouTubeResult : PlatformResult, ISupportsCaching
{
    /// <summary>The webm cache is the local-cache getter's, which is always among this result's downloaders.</summary>
    public Task RunCacheProcess(StreamSpreader streamSpreader)
    {
        Downloaders.OfType<GetterLocalCache>().FirstOrDefault()?.Keep(this, streamSpreader);
        return Task.CompletedTask;
    }

    public override string GetDownloadUrl()
    {
        return $"https://www.youtube.com/watch?v={GetPureId()}";
    }
}