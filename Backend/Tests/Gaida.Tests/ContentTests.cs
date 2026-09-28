using Gaida.API.Controllers;

namespace Gaida.Tests;

public class ContentTests
{
    [Theory]
    [InlineData("Opus")]
    [InlineData("opus")]
    [InlineData("OPUS")]
    public void ACodecIsTheSameCodecInAnyCase(string codec)
    {
        Assert.Equal(("audio/ogg", "-c:a libopus", "-f ogg"), Content.Encoding(codec));
    }

    [Fact]
    public void ACodecThisDoesNotEncodeIsRefused()
    {
        Assert.Null(Content.Encoding("mka"));
    }
}
