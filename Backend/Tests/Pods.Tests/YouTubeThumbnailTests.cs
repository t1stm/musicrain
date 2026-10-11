using Gaida.Platforms.YouTube.SearchProviders;
using YoutubeExplode.Common;

namespace Pods.Tests;

public class YouTubeThumbnailTests
{
    [Fact]
    public void TheLargestThumbnailWinsWithoutItsQueryString()
    {
        Thumbnail[] thumbnails =
        [
            new("https://i.ytimg.com/vi/x/default.jpg?sqp=small", new Resolution(120, 90)),
            new("https://i.ytimg.com/vi/x/maxresdefault.jpg?sqp=big", new Resolution(1280, 720)),
            new("https://i.ytimg.com/vi/x/hqdefault.jpg", new Resolution(480, 360))
        ];

        Assert.Equal("https://i.ytimg.com/vi/x/maxresdefault.jpg",
            YouTubeSearchProviderExplode.BestThumbnail(thumbnails));
    }

    [Fact]
    public void AVideoWithNoThumbnailsHasNone()
    {
        Assert.Null(YouTubeSearchProviderExplode.BestThumbnail([]));
    }
}
