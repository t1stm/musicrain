namespace Dunav;

/// <summary>The log lines Dunav writes through Microsoft.Extensions.Logging, source-generated.</summary>
internal static partial class Logs
{
    [LoggerMessage(LogLevel.Information, "Preloading '{ID}' {Codec} {Bitrate}")]
    public static partial void Preloading(this ILogger logger, string id, string codec, int bitrate);

    [LoggerMessage(LogLevel.Information, "Cache entry {Key} was evicted before it could be served")]
    public static partial void EvictedBeforeServed(this ILogger logger, string key);
}
