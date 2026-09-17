using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Selo.Controllers.Helpers;
using Selo.Multiplayer;

namespace Selo.Controllers;

public class Multiplayer(ILogger<Multiplayer> logger, MultiplayerManager manager) : ControllerBase
{
    [HttpPost("/Audio/Multiplayer/CreateRoom")]
    public async Task<IActionResult> CreateRoom()
    {
        var roomId = await manager.CreateNewRoom();
        logger.LogInformation("Room created: {Room}", roomId);

        return new JsonResult(manager.GetRoom(roomId));
    }

    [HttpGet("/Audio/Multiplayer/Rooms")]
    public async Task<IActionResult> Rooms()
    {
        try
        {
            if (!HttpContext.WebSockets.IsWebSocketRequest) return new BadRequestResult();

            using var webSocket = await HttpContext.WebSockets.AcceptWebSocketAsync();
            logger.LogDebug("Room update websocket '{ID}' connected, with IP: {IP}", HttpContext.TraceIdentifier,
                HttpContext.Connection.RemoteIpAddress);

            await HandleRoomUpdateWebSocket(webSocket, HttpContext.RequestAborted);
        }
        catch (Exception e)
        {
            logger.LogError(e, "WebSocket '{ID}' encountered error", HttpContext.TraceIdentifier);
            throw;
        }

        // the response is the socket itself; a status result on top of it would throw
        return new EmptyResult();
    }

    [HttpGet("/Audio/Multiplayer/Join")]
    public async Task<IActionResult> Join(string room, string? username)
    {
        try
        {
            if (!HttpContext.WebSockets.IsWebSocketRequest || !Guid.TryParse(room, out var guid)) return BadRequest();

            using var webSocket = await HttpContext.WebSockets.AcceptWebSocketAsync();
            logger.LogDebug("WebSocket '{ID}' connected, with IP: {IP}", HttpContext.TraceIdentifier,
                HttpContext.Connection.RemoteIpAddress);
            await HandleRoomJoinWebSocket(webSocket, guid, username, HttpContext.TraceIdentifier,
                HttpContext.RequestAborted);
        }
        catch (Exception e)
        {
            logger.LogError(e, "WebSocket '{ID}' encountered error", HttpContext.TraceIdentifier);
            throw;
        }

        // the response is the socket itself; a status result on top of it would throw
        return new EmptyResult();
    }

    private async Task HandleRoomUpdateWebSocket(WebSocket webSocket, CancellationToken cancellationToken)
    {
        var user = new User
        {
            Id = "dummy user",
            WebSocket = webSocket
        };

        // Pushed by the manager whenever a room is added or renamed; no polling.
        await SendRooms();
        manager.RoomsChanged += SendRooms;

        try
        {
            // Keep a pending receive so control frames (including keepalive pongs) are drained
            // and this socket gets the same dead-client detection as room sockets.
            using var reader = new WebSocketTextReader();
            while (await reader.ReadWholeMessageAsync(webSocket, cancellationToken) is not null)
            {
            }
        }
        finally
        {
            manager.RoomsChanged -= SendRooms;
        }

        await CloseNormallyAsync(webSocket);
        return;

        Task SendRooms()
        {
            var message = new Utf8Message(1024);
            using (var writer = new Utf8JsonWriter(message))
            {
                JsonSerializer.Serialize(writer, manager.GetRooms());
            }

            return user.SendAsync(message);
        }
    }

    private async Task HandleRoomJoinWebSocket(WebSocket webSocket, Guid roomId, string? username, string id,
        CancellationToken cancellationToken)
    {
        try
        {
            using var reader = new WebSocketTextReader();
            var open = await HandleUserMessage(id, roomId, webSocket, default, username);

            while (open)
            {
                var message = await reader.ReadWholeMessageAsync(webSocket, cancellationToken);
                if (message is null) break;

                open = await HandleUserMessage(id, roomId, webSocket, message.Value);
            }

            await CloseNormallyAsync(webSocket);
        }
        finally
        {
            // The user is in the store from the first HandleUserMessage onward, so
            // anything it throws afterwards — a send onto a socket the peer has
            // already dropped, a lookup that fails — used to strand them there.
            // The barriers count against the live member list, so one stranded
            // member is a room that never advances past its current track again.
            var room = manager.GetRoom(roomId);
            await (room?.RemoveUser(id) ?? Task.CompletedTask);

            logger.LogDebug("WebSocket '{ID}' disconnected", id);
        }
    }

    private static async Task CloseNormallyAsync(WebSocket webSocket)
    {
        if (webSocket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;

        try
        {
            await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
        }
        catch (WebSocketException)
        {
            // The peer may have disappeared between checking State and closing. The caller's
            // finally block still removes a joined user from the room.
        }
    }

    /// <returns>Whether the room is still open.</returns>
    private async Task<bool> HandleUserMessage(string id, Guid roomId, WebSocket webSocket,
        ReadOnlyMemory<char> message, string? initialUsername = null)
    {
        // guarded: the frame only becomes a string when someone is actually listening for it
        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebug("WebSocket '{ID}' received: '{Message}'", id, message.ToString());

        var room = manager.GetRoom(roomId);
        if (room is null) return false;

        var user = await room.GetOrAddUser(id, webSocket, initialUsername);
        await room.HandleUserMessage(user, message);

        return true;
    }
}