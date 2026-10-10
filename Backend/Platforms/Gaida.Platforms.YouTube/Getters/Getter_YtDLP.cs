using System.Diagnostics;
using Gaida.Core.Platforms;
using Gaida.Core.Streams;
using Serilog;

namespace Gaida.Platforms.YouTube.Getters;

public sealed class GetterYtDlp(ILogger logger) : ContentGetter(logger)
{
    public override int Priority => 20;

    public override Task<StreamSpreader?> GetContentDataAsync(PlatformResult result,
        CancellationToken cancellationToken)
    {
        var url = result.GetDownloadUrl();
        var process = Process.Start(new ProcessStartInfo("yt-dlp")
        {
            ArgumentList = { "-q", "--no-warnings", "-r", "4.0M", "-f", "bestaudio", url, "-o", "-" },
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            UseShellExecute = false
        });
        if (process is null)
        {
            Logger.Error("Failed to start yt-dlp for URL: {URL}", url);
            return Task.FromResult<StreamSpreader?>(null);
        }

        var streamSpreader = new StreamSpreader();
        _ = Task.Run(PumpAsync, CancellationToken.None);

        return Task.FromResult<StreamSpreader?>(streamSpreader);

        async Task PumpAsync()
        {
            using (process)
            {
                try
                {
                    await process.StandardOutput.BaseStream.CopyToAsync(streamSpreader, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // The request that started it left; the body ends where it got to.
                }
                catch (Exception e)
                {
                    Logger.Error(e, "yt-dlp failed for URL: {URL}", url);
                }
                finally
                {
                    if (!process.HasExited) process.Kill(true);
                    await streamSpreader.CloseAsync();
                }
            }
        }
    }
}
