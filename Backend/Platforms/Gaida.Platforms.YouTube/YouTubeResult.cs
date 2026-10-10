using Gaida.Core.Platforms;
using Gaida.Core.Platforms.Optional.Supports;
using Gaida.Core.Streams;
using Gaida.Platforms.YouTube.Getters;

namespace Gaida.Platforms.YouTube;

public sealed class YouTubeResult : PlatformResult, ISupportsCaching
{
    public const string Prefix = "yt://";
    public const string PlaylistPrefix = "yt-playlist://";

    /// <summary>The webm cache is the local-cache getter's, which is always among this result's downloaders.</summary>
    public void RunCacheProcess(StreamSpreader streamSpreader)
    {
        Downloaders.OfType<GetterLocalCache>().FirstOrDefault()?.Keep(this, streamSpreader);
    }

    public string GetDownloadUrl()
    {
        return $"https://www.youtube.com/watch?v={GetPureId()}";
    }
}