namespace Gaida.API;

/// <summary>The log lines Gaida.API writes through Microsoft.Extensions.Logging, source-generated.</summary>
internal static partial class Logs
{
    [LoggerMessage(LogLevel.Information, "Searching for {Query}")]
    public static partial void Searching(this ILogger logger, string query);

    [LoggerMessage(LogLevel.Warning, "Classify rejected {Query}: {Error}")]
    public static partial void ClassifyRejected(this ILogger logger, string query, string? error);

    [LoggerMessage(LogLevel.Error, "Search failed for {Query}")]
    public static partial void SearchFailed(this ILogger logger, Exception exception, string query);

    [LoggerMessage(LogLevel.Information, "Resolving {Count} ids")]
    public static partial void Resolving(this ILogger logger, int count);

    [LoggerMessage(LogLevel.Warning, "Resolving {Id} failed")]
    public static partial void ResolvingFailed(this ILogger logger, Exception exception, string id);

    [LoggerMessage(LogLevel.Information, "Returning {Count} random results with a {Share} YouTube share")]
    public static partial void ReturningRandom(this ILogger logger, int count, double share);

    [LoggerMessage(LogLevel.Information, "Downloading Raw '{ID}'")]
    public static partial void DownloadingRaw(this ILogger logger, string id);

    [LoggerMessage(LogLevel.Information, "Finishing '{ID}' took '{Duration}'")]
    public static partial void Finished(this ILogger logger, string id, TimeSpan duration);

    [LoggerMessage(LogLevel.Information, "Downloading '{ID}' {Codec} {Bitrate}")]
    public static partial void Downloading(this ILogger logger, string id, string codec, int bitrate);

    [LoggerMessage(LogLevel.Error, "Failed to resolve query {Query}")]
    public static partial void QueryFailed(this ILogger logger, Exception exception, string query);

    [LoggerMessage(LogLevel.Debug, "Nothing playable for {ID} ({Artist} — {Name})")]
    public static partial void NothingPlayable(this ILogger logger, string? id, string? artist, string? name);
}
