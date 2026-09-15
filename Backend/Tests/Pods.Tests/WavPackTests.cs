using Gaida.Platforms.MusicDatabase.Manager;

namespace Pods.Tests;

public class WavPackTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("wavpack-tests").FullName;

    public void Dispose()
    {
        Directory.Delete(_folder, true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void CorrectionForFindsTheSiblingOfAHybridTrack()
    {
        var track = Touch("hybrid.wv");
        var correction = Touch("hybrid.wvc");

        Assert.Equal(correction, WavPack.CorrectionFor(track));
    }

    [Fact]
    public void CorrectionForIgnoresALosslessWavPackTrack()
    {
        Assert.Null(WavPack.CorrectionFor(Touch("lossless.wv")));
    }

    [Fact]
    public void CorrectionForIgnoresEveryOtherFormat()
    {
        var track = Touch("song.flac");
        Touch("song.flacc"); // the +"c" rule must not fire on anything but .wv

        Assert.Null(WavPack.CorrectionFor(track));
    }

    private string Touch(string name)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, []);
        return path;
    }
}
