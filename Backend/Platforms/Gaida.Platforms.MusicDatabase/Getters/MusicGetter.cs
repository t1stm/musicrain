using System.Diagnostics;
using Gaida.Core.FFmpeg;
using Gaida.Core.Platforms;
using Gaida.Core.Streams;
using Gaida.Platforms.MusicDatabase.Manager;
using Serilog;

namespace Gaida.Platforms.MusicDatabase.Getters;

public class MusicGetter(ILogger logger) : ContentGetter(logger)
{
    public override int Priority => 99;

    public override Task<StreamSpreader?> GetContentDataAsync(PlatformResult result,
        CancellationToken cancellationToken)
    {
        if (result is not MusicResult localResult)
        {
            Logger.Error("MusicGetter: Wrong result type. Expected MusicResult, got {Type}", result.GetType().Name);
            return Task.FromResult<StreamSpreader?>(null);
        }

        Logger.Debug("MusicGetter: Attempting to get content data for: {Path}", localResult.Path);
        if (!File.Exists(localResult.Path))
        {
            Logger.Error("MusicGetter: File not found at path: {Path}", localResult.Path);
            return Task.FromResult<StreamSpreader?>(null);
        }

        // The library file is the body. Adopting it copies nothing and allocates nothing: the spreader
        // reads straight out of it, and never writes to or deletes it.
        if (WavPack.CorrectionFor(localResult.Path) is null)
            return Task.FromResult<StreamSpreader?>(StreamSpreader.FromExistingFile(localResult.Path));

        return Task.FromResult<StreamSpreader?>(DecodedWavPack(localResult.Path, cancellationToken));
    }

    /// <summary>
    ///     A hybrid WavPack track decoded through wvunpack and re-wrapped as FLAC. ffmpeg's own WavPack
    ///     decoder ignores the .wvc correction file and would hand back the lossy core, which is the
    ///     whole reason this path exists; wvunpack picks the correction up on its own from beside the .wv.
    /// </summary>
    private StreamSpreader DecodedWavPack(string path, CancellationToken cancellationToken)
    {
        var process = Process.Start(new ProcessStartInfo("wvunpack", $"-q -o - \"{path}\"")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false
        });

        if (process is null)
        {
            // A missing wvunpack is a lossy track, not a dead one.
            Logger.Error("MusicGetter: wvunpack failed to start for {Path}, serving the lossy core", path);
            return StreamSpreader.FromExistingFile(path);
        }

        var spreader = new StreamSpreader();
        _ = Task.Run(async () =>
        {
            // ponytail: FLAC out of the pod rather than wvunpack's raw WAV — a 5-minute track is ~50MB of
            // PCM, which Dunav would then cache and count against its byte ceiling, for no audible
            // difference. ffmpeg's FLAC encoder takes integer samples only, so a 32-bit float WavPack file
            // would fail the encode; the library has none, and the exit code below is what says so.
            await FFmpegEncoder.EncodeAsync(process.StandardOutput.BaseStream, spreader, "-c:a flac -f flac",
                cancellationToken);

            if (process.ExitCode != 0)
                Logger.Error("MusicGetter: wvunpack exited {Code} for {Path}", process.ExitCode, path);

            await spreader.CloseAsync();
        }, cancellationToken);

        return spreader;
    }
}
