using Gaida.Core.Platforms;
using Gaida.Core.Streams;
using Gaida.Platforms.YouTube.Search_Providers;
using Serilog;
using YoutubeExplode;

namespace Gaida.Platforms.YouTube.Getters;

public class GetterYouTubeExplode(ILogger logger) : ContentGetter(logger)
{
    public override int Priority => 40;
    private static YoutubeClient Client => YouTubeSearchProviderExplode.Client;

    public override async Task<StreamSpreader?> GetContentDataAsync(PlatformResult result,
        CancellationToken cancellationToken)
    {
        if (result is not YouTubeResult youtubeResult)
        {
            Logger.Debug("Result is not a YouTubeResult");
            return null;
        }

        try
        {
            var manifest = await Client.Videos.Streams.GetManifestAsync(
                youtubeResult.GetPureId().ToString(), cancellationToken);

            var chosenAudioOnlyStream = manifest.GetAudioOnlyStreams()
                .MaxBy(s => s.Bitrate.KiloBitsPerSecond * (s.AudioCodec is "Opus" ? 2 : 1));

            if (chosenAudioOnlyStream is null)
            {
                Logger.Error("Failed to find audio-only stream for YouTube video ID: {ID}", youtubeResult.Id);
                return null;
            }

            var streamSpreader = new StreamSpreader();
            _ = Task.Run(async () =>
            {
                try
                {
                    await Client.Videos.Streams.CopyToAsync(chosenAudioOnlyStream, streamSpreader,
                        cancellationToken: cancellationToken);
                    Logger.Debug("Downloaded audio-only stream for YouTube video ID: {ID}", youtubeResult.Id);
                }
                catch (Exception e)
                {
                    Logger.Fatal(e, "Error while copying stream for YouTube video ID: {ID}", youtubeResult.Id);
                }
                finally
                {
                    await streamSpreader.CloseAsync();
                }
            }, cancellationToken);

            return streamSpreader;
        }
        catch (Exception e)
        {
            Logger.Fatal(e, "Error while processing YouTube video ID: {ID}", youtubeResult.Id);
            return null;
        }
    }
}
