using System.Globalization;

namespace Stih;

/// <summary>
///     Stih's settings, under the keys it has always read. The sweep's numbers are kept as text and read leniently,
///     as they always were: a value that does not parse is the default, not a failed boot.
/// </summary>
public sealed class StihOptions
{
    /// <summary>Empty disables every outbound lookup.</summary>
    [ConfigurationKeyName("LRCLIB_URL")]
    public string LrcLibUrl { get; set; } = string.Empty;

    [ConfigurationKeyName("LRCLIB_USER_AGENT")]
    public string LrcLibUserAgent { get; set; } = "musicrain/1.0 (+https://github.com/t1stm/musicrain)";

    /// <summary>The library's own volume, where a track's lyrics sit beside its audio.</summary>
    [ConfigurationKeyName("MUSIC_LIBRARY")]
    public string MusicLibrary { get; set; } = "/music";

    /// <summary>Stih's own volume: the index and the lyrics of tracks with no library file.</summary>
    [ConfigurationKeyName("LYRICS_DATA")]
    public string LyricsData { get; set; } = "/lyrics";

    [ConfigurationKeyName("Local:Url")]
    public string? LocalUrl { get; set; }

    [ConfigurationKeyName("Deezer:Url")]
    public string? DeezerUrl { get; set; }

    /// <summary>The header Oko uses, wanted by <c>/register</c> when set.</summary>
    [ConfigurationKeyName("ADMIN_TOKEN")]
    public string? AdminToken { get; set; }

    [ConfigurationKeyName("LYRICS_SWEEP")]
    public string? Sweep { get; set; }

    [ConfigurationKeyName("LYRICS_SWEEP_DELAY_MS")]
    public string? SweepDelayMs { get; set; }

    [ConfigurationKeyName("LYRICS_SWEEP_IDLE_HOURS")]
    public string? SweepIdleHours { get; set; }

    [ConfigurationKeyName("LYRICS_RETRY_DAYS")]
    public string? RetryDays { get; set; }

    /// <summary>On unless the setting is a boolean that says off.</summary>
    public bool SweepEnabled => Sweep is null || !bool.TryParse(Sweep, out var on) || on;

    public TimeSpan SweepDelay => TimeSpan.FromMilliseconds(
        int.TryParse(SweepDelayMs, CultureInfo.InvariantCulture, out var ms) ? Math.Max(0, ms) : 1000);

    public TimeSpan SweepIdle => TimeSpan.FromHours(
        double.TryParse(SweepIdleHours, CultureInfo.InvariantCulture, out var hours) ? Math.Max(0.1, hours) : 6);

    /// <summary>The retry window as the sweep asks the library for it, in whole days.</summary>
    public int SweepRetryDays => int.TryParse(RetryDays, CultureInfo.InvariantCulture, out var days) ? days : 30;

    /// <summary>The retry window as a remembered miss is judged against it, fractions of a day included.</summary>
    public TimeSpan RetryAfter =>
        TimeSpan.FromDays(double.TryParse(RetryDays, CultureInfo.InvariantCulture, out var days) ? days : 30);
}
