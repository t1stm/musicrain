namespace Gaida.Bot.Players;

/// <summary>The playback knobs an operator can turn, read once at startup.</summary>
/// <param name="Bitrate">
///     <c>GAIDA_OPUS_BITRATE</c>: what every track is encoded at, in kbps, instead of the voice channel's own.
/// </param>
/// <param name="StatusbarIntervalMs"><c>GAIDA_STATUSBAR_INTERVAL_MS</c>: the least time between statusbar edits.</param>
public sealed record PlaybackSettings(int? Bitrate = null, int StatusbarIntervalMs = PlaybackSettings.DefaultStatusbarIntervalMs)
{
    public const int DefaultStatusbarIntervalMs = 3200;
}
