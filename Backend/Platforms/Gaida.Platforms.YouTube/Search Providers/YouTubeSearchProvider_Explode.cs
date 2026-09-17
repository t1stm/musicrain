using System.Runtime.CompilerServices;
using Gaida.Core.Platforms;
using Gaida.Core.Platforms.Optional.Supports;
using Gaida.Core.Utils;
using Serilog;
using YoutubeExplode;
using YoutubeExplode.Common;

namespace Gaida.Platforms.YouTube.Search_Providers;

public sealed class YouTubeSearchProviderExplode(ILogger logger) : SearchProvider(logger),
    ISupportsId, ISupportsPlaylist, ISupportsSearch
{
    private const int MaxKeywordResults = 15;
    public static YoutubeClient Client { get; } = new();
    public override string PlatformIdentifier => "yt://";
    public override int Priority => 40;

    public async Task<PlatformResult?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        var video = await Client.Videos.GetAsync(id, cancellationToken);

        return new YouTubeResult
        {
            Id = PlatformIdentifier + id,
            Name = video.Title,
            Artist = video.Author.ChannelTitle,
            Duration = video.Duration.GetValueOrDefault(TimeSpan.Zero),
            ThumbnailUrl = BestThumbnail(video.Thumbnails),
            Downloaders = ContentDownloaders
        };
    }

    public async IAsyncEnumerable<PlatformResult> SearchPlaylist(string playlistUrl,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var playlistId = playlistUrl.StartsWith("yt-playlist://", StringComparison.OrdinalIgnoreCase)
            ? playlistUrl["yt-playlist://".Length..]
            : playlistUrl.AsSpan().SliceAfter("list=").SliceTo("&").ToString();

        if (string.IsNullOrWhiteSpace(playlistId)) yield break;

        await foreach (var batch in Client.Playlists.GetVideoBatchesAsync(playlistId, cancellationToken))
        foreach (var video in batch.Items)
            yield return new YouTubeResult
            {
                Id = PlatformIdentifier + video.Id,
                Name = video.Title,
                Artist = video.Author.ChannelTitle,
                Duration = video.Duration.GetValueOrDefault(TimeSpan.Zero),
                ThumbnailUrl = BestThumbnail(video.Thumbnails),
                Downloaders = ContentDownloaders
            };
    }

    public async IAsyncEnumerable<PlatformResult> SearchKeywords(string keywords,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var returned = 0;
        await foreach (var video in Client.Search.GetVideosAsync(keywords, cancellationToken))
        {
            yield return new YouTubeResult
            {
                Id = PlatformIdentifier + video.Id,
                Name = video.Title,
                Artist = video.Author.ChannelTitle,
                Duration = video.Duration.GetValueOrDefault(TimeSpan.Zero),
                ThumbnailUrl = BestThumbnail(video.Thumbnails),
                Downloaders = ContentDownloaders
            };

            if (++returned >= MaxKeywordResults) yield break;
        }
    }

    /// <summary>Highest resolution thumbnail, without the tracking query string.</summary>
    private static string BestThumbnail(IReadOnlyList<Thumbnail> thumbnails)
    {
        return thumbnails.MaxBy(t => t.Resolution.Area)!.Url.AsSpan().SliceTo("?").ToString();
    }
}