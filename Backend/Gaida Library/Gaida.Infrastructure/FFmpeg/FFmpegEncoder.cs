using System.Diagnostics;

namespace Gaida.Infrastructure.FFmpeg;

public static class FFmpegEncoder
{
    /// <summary>Pipes <paramref name="source" /> through ffmpeg and copies the result into <paramref name="destination" />.</summary>
    public static async Task EncodeAsync(Stream source, Stream destination, string ffmpegArguments,
        CancellationToken cancellationToken = default)
    {
        using var process = Process.Start(new ProcessStartInfo("ffmpeg",
            $"-v quiet -nostats -i - {ffmpegArguments} pipe:1")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        });
        ArgumentNullException.ThrowIfNull(process);

        // ffmpeg only flushes its last packets once stdin reaches EOF, so the close has to happen even
        // when the copy failed — otherwise the process sits there holding the tail of the encode and the
        // output stream never closes.
        var feed = source.CopyToAsync(process.StandardInput.BaseStream, cancellationToken)
            .ContinueWith(_ => { process.StandardInput.BaseStream.Close(); }, CancellationToken.None);

        try
        {
            await process.StandardOutput.BaseStream.CopyToAsync(destination, cancellationToken);
            await feed;
        }
        catch
        {
            // Nobody is reading its output any more, and a full stdout pipe would hold it there for good.
            if (!process.HasExited) process.Kill(true);
            throw;
        }
    }
}
