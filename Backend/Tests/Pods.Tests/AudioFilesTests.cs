using Gaida.Platforms.MusicDatabase;

namespace Pods.Tests;

public class AudioFilesTests
{
    [Theory]
    [InlineData("Artist/Song.flac", true)]
    [InlineData("Artist/Song.wv", true)]
    [InlineData("Artist/Song.FLAC", false)]
    [InlineData("Artist/Song.wvc", false)]
    [InlineData("Artist/cover.jpg", false)]
    public void TheScanIndexesOnlyKnownExtensionsInLowerCase(string file, bool indexed)
    {
        Assert.Equal(indexed, AudioFiles.IsAudio(file));
    }

    [Theory]
    [InlineData(".flac", "audio/flac")]
    [InlineData(".MP3", "audio/mpeg")]
    [InlineData(".wv", "audio/x-wavpack")]
    [InlineData(".txt", "application/octet-stream")]
    public void AnExtensionIsServedAsItsType(string extension, string contentType)
    {
        Assert.Equal(contentType, AudioFiles.ContentTypeFor(extension));
    }
}
