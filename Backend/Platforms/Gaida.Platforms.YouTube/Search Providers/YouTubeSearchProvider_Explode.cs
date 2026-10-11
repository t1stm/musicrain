using System.Runtime.CompilerServices;
using Gaida.Core.Platforms;
using Gaida.Core.Platforms.Optional.Supports;
using Gaida.Core.Utils;
using Serilog;
using YoutubeExplode;
using YoutubeExplode.Common;
using YoutubeExplode.Videos;

namespace Gaida.Platforms.YouTube.SearchProviders;

public sealed class YouTubeSearchProviderExplode(ILogger logger, YoutubeClient client) : SearchProvider(logger),
    ISupportsId, ISupportsPlaylist, ISupportsSearch
{
    private const int MaxKeywordResults = 15;
    public override int Priority => 40;

    public async Task<PlatformResult?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        return Result(await client.Videos.GetAsync(id, cancellationToken));
    }

    public async IAsyncEnumerable<PlatformResult> SearchPlaylist(string playlist,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var playlistId = playlist.StartsWith(YouTubeResult.PlaylistPrefix, StringComparison.OrdinalIgnoreCase)
            ? playlist[YouTubeResult.PlaylistPrefix.Length..]
            : playlist.AsSpan().SliceAfter("list=").SliceTo("&").ToString();

        if (string.IsNullOrWhiteSpace(playlistId)) yield break;

        await foreach (var batch in client.Playlists.GetVideoBatchesAsync(playlistId, cancellationToken))
            foreach (var video in batch.Items)
                yield return Result(video);
    }

    public async IAsyncEnumerable<PlatformResult> SearchKeywords(string keywords,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var returned = 0;
        await foreach (var video in client.Search.GetVideosAsync(keywords, cancellationToken))
        {
            yield return Result(video);

            if (++returned >= MaxKeywordResults) yield break;
        }
    }

    private YouTubeResult Result(IVideo video)
    {
        return new YouTubeResult
        {
            Id = YouTubeResult.Prefix + video.Id,
            Name = video.Title,
            Artist = video.Author.ChannelTitle,
            Duration = video.Duration.GetValueOrDefault(TimeSpan.Zero),
            ThumbnailUrl = BestThumbnail(video.Thumbnails),
            Downloaders = ContentDownloaders
        };
    }

    /// <summary>Highest resolution thumbnail, without the tracking query string.</summary>
    internal static string? BestThumbnail(IReadOnlyList<Thumbnail> thumbnails)
    {
        return thumbnails.MaxBy(t => t.Resolution.Area)?.Url.AsSpan().SliceTo("?").ToString();
    }
}