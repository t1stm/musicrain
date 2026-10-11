using System.Net;

namespace Selo;

/// <summary>The log lines Selo writes through Microsoft.Extensions.Logging, source-generated.</summary>
internal static partial class Logs
{
    [LoggerMessage(LogLevel.Information, "Room created: {Room}")]
    public static partial void RoomCreated(this ILogger logger, Guid room);

    [LoggerMessage(LogLevel.Debug, "Room update websocket '{ID}' connected, with IP: {IP}")]
    public static partial void RoomListConnected(this ILogger logger, string id, IPAddress? ip);

    [LoggerMessage(LogLevel.Debug, "WebSocket '{ID}' connected, with IP: {IP}")]
    public static partial void SocketConnected(this ILogger logger, string id, IPAddress? ip);

    [LoggerMessage(LogLevel.Error, "WebSocket '{ID}' encountered error")]
    public static partial void SocketFailed(this ILogger logger, Exception exception, string id);

    [LoggerMessage(LogLevel.Debug, "WebSocket '{ID}' disconnected")]
    public static partial void SocketDisconnected(this ILogger logger, string id);

    [LoggerMessage(LogLevel.Debug, "WebSocket '{ID}' received: '{Message}'")]
    public static partial void SocketReceived(this ILogger logger, string id, string message);

    [LoggerMessage(LogLevel.Debug, "Sending to {User} failed")]
    public static partial void SendFailed(this ILogger logger, Exception exception, string user);

    [LoggerMessage(LogLevel.Debug, "A room-list socket could not be told about a change")]
    public static partial void RoomListNotTold(this ILogger logger, Exception exception);

    [LoggerMessage(LogLevel.Warning, "Room {Room} could not look up {Id}")]
    public static partial void LookupFailed(this ILogger logger, Exception exception, Guid room, string id);
}
