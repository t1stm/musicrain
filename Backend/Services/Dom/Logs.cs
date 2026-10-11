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
}
