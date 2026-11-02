namespace Dom;

/// <summary>The log lines Dom writes through Microsoft.Extensions.Logging, source-generated.</summary>
internal static partial class Logs
{
    [LoggerMessage(LogLevel.Information, "New account: {Username}")]
    public static partial void NewAccount(this ILogger logger, string username);

    [LoggerMessage(LogLevel.Information, "{Username} changed their password")]
    public static partial void PasswordChanged(this ILogger logger, string username);

    [LoggerMessage(LogLevel.Information, "{Previous} is now {Username}")]
    public static partial void Renamed(this ILogger logger, string previous, string username);

    [LoggerMessage(LogLevel.Warning, "Moliv did not forget the history of account {Id}")]
    public static partial void HistoryNotForgotten(this ILogger logger, Exception exception, string id);

    [LoggerMessage(LogLevel.Information, "Gaida:Url is unset, so playlist tracks keep the names they were saved with")]
    public static partial void RefreshOff(this ILogger logger);

    [LoggerMessage(LogLevel.Information, "Playlist refresh: {Changed} track(s) changed, {Resolved} of {Ids} ID(s) resolved")]
    public static partial void Refreshed(this ILogger logger, int changed, int resolved, int ids);

    [LoggerMessage(LogLevel.Warning, "Playlist refresh: the fifty IDs from {First} were not resolved")]
    public static partial void RefreshChunkFailed(this ILogger logger, Exception exception, string first);

    [LoggerMessage(LogLevel.Warning, "The playlist refresh failed; trying again tomorrow")]
    public static partial void RefreshFailed(this ILogger logger, Exception exception);
}
