using Microsoft.Extensions.Logging;

namespace Gaida.Admin;

/// <summary>The log lines the admin surface writes, source-generated.</summary>
internal static partial class Logs
{
    [LoggerMessage(LogLevel.Information, "ADMIN_TOKEN is not set — the /Admin surface is disabled")]
    public static partial void AdminDisabled(this ILogger logger);
}
