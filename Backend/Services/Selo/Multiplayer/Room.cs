using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Selo.Multiplayer.Handlers;

namespace Selo.Multiplayer;

public class Room
{
    private readonly HttpClient _gaida;
    [JsonIgnore] private readonly VirtualPlayer _player;
    [JsonIgnore] private readonly MessageQueue _queue;

    [JsonIgnore] private readonly UserStore _store;

    public Room(Guid guid, HttpClient gaida)
    {
        RoomId = guid;
        _gaida = gaida;
        RoomName = guid.ToString();

        _store = new UserStore();
        _queue = new MessageQueue(_store);
        _player = new VirtualPlayer(_queue);
    }

    [JsonInclude]
    [JsonPropertyName("roomID")]
    public Guid RoomId { get; init; }

    [JsonInclude]
    [JsonPropertyName("name")]
    public string RoomName { get; set; }

    [JsonInclude]
    [JsonPropertyName("description")]
    public string RoomDescription { get; set; } = "";

    [JsonIgnore] public Action? OnInfoModified { get; init; }

    /// <summary>
    ///     Raised once the last member is gone, so an abandoned room does not sit in the list — and
    ///     hold its queue and its clock — for the life of the process.
    /// </summary>
    [JsonIgnore]
    public Action? OnEmptied { get; init; }

    /// <summary>
    ///     Everything the admin panel shows for this room. Deliberately reads <see cref="User.Username" />
    ///     rather than <see cref="User.ChatUsername" />: the latter assigns an anonymous name as a side
    ///     effect, and a monitoring read must not change what the room calls people.
    /// </summary>
    public async Task<object> Snapshot()
    {
        return new
        {
            roomId = RoomId,
            name = RoomName,
            description = RoomDescription,
            users = _store.Users.Values.Select(user => new
            {
                id = user.Id,
                username = user.Username,
                socket = user.WebSocket.State.ToString()
            }).ToList(),
            player = await _player.Snapshot()
        };
    }

    /// <summary>
    ///     Removes a member and closes their socket. The close is the point: <see cref="RemoveUser" />
    ///     alone drops them from the store, and <see cref="GetOrAddUser" /> puts them straight back on
    ///     their next frame — a kick that only lasts until the kicked person presses play is not a kick.
    /// </summary>
    public async Task<bool> Kick(string id)
    {
        var user = _store.GetUser(id);
        if (user is null) return false;

        await RemoveUser(id);

        try
        {
            // CloseOutputAsync, never CloseAsync: CloseAsync sends the close frame and then waits for
            // the peer's acknowledgement, and that acknowledgement is read by this member's own
            // receive loop, which is already sitting in ReadWholeMessageAsync. The wait therefore
            // never completes and the operator's request hangs until it times out — the kick itself
            // works, so the only symptom is a 502 on an action that succeeded. Sending the frame is
            // all that is wanted here; the receive loop sees the peer's reply and tears the
            // connection down through its own finally.
            if (user.WebSocket.State == WebSocketState.Open)
                await user.WebSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure,
                    "Removed by an administrator", CancellationToken.None);
        }
        catch (WebSocketException)
        {
            // Already gone. They are out of the store either way, which is what was asked for.
        }
        catch (ObjectDisposedException)
        {
            // The receive loop's `using` got there first. Same outcome.
        }

        return true;
    }

    /// <summary>Everyone currently in the room, for a close that has to remove them all.</summary>
    public IReadOnlyCollection<string> UserIds => [.. _store.Users.Keys];

    public ValueTask<User> GetOrAddUser(string id, WebSocket webSocket, string? initialUsername)
    {
        // the join callback closes over the username, so building it on every message — which
        // is what the unconditional call did — allocated a closure and a delegate per frame
        if (_store.GetUser(id) is { } present) return new ValueTask<User>(present);

        return _store.GetOrAddUser(id, webSocket, user =>
        {
            user.Username = initialUsername;
            return _player.Joined(user);
        });
    }

    public async Task RemoveUser(string id)
    {
        var user = _store.GetUser(id);
        // a socket can close without ever having joined, so let's just return in that case
        if (user is null) return;

        await _store.RemoveUser(id);
        await _queue.Send($"chat System %% User '{user.ChatUsername}' left from the session.");
        await _player.UserLeft(id);

        if (_store.Count == 0)
            OnEmptied?.Invoke();
    }

    public Task HandleUserMessage(User user, string message)
    {
        return HandleUserMessage(user, message.AsMemory());
    }

    /// <summary>
    ///     Takes the frame as memory over the reader's buffer: splitting a command used to cut two
    ///     strings out of every inbound message, and this path runs once per keystroke-rate action
    ///     from every member of every room.
    /// </summary>
    public Task HandleUserMessage(User user, ReadOnlyMemory<char> message)
    {
        var splitIndex = message.Span.IndexOf(' ');

        return splitIndex != -1
            ? HandleParameterMessages(message[..splitIndex], message[splitIndex..], user)
            : HandleParameterlessMessages(message, user);
    }

    private Task HandleParameterMessages(ReadOnlyMemory<char> name, ReadOnlyMemory<char> value, User user)
    {
        return name.Span switch
        {
            "add" => Enqueue(value.ToString()),
            "addnext" => Enqueue(value.ToString(), true),
            "setnext" when int.TryParse(value.Span, out var nextIndex) => _player.SetNext(nextIndex),
            "move" when TryParseMove(value.Span, out var from, out var to) => _player.Move(from, to),
            "skipto" when int.TryParse(value.Span, out var skipIndex) => _player.SkipTo(skipIndex),
            "seek" when double.TryParse(value.Span, out var seekSeconds) => _player.SeekTo(seekSeconds),
            "remove" when int.TryParse(value.Span, out var removeIndex) => _player.Remove(removeIndex),
            "chat" => _queue.Send($"chat {user.ChatUsername} %% {value}"),
            "updateroom" => HandleUpdateRoom(value, user),
            _ => Task.CompletedTask
        };
    }

    /// <summary>Two indexes in one argument, <c>move &lt;from&gt; &lt;to&gt;</c>.</summary>
    private static bool TryParseMove(ReadOnlySpan<char> value, out int from, out int to)
    {
        from = 0;
        to = 0;

        var argument = value.Trim();
        var splitIndex = argument.IndexOf(' ');

        return splitIndex != -1
               && int.TryParse(argument[..splitIndex], out from)
               && int.TryParse(argument[(splitIndex + 1)..], out to);
    }

    private async Task Enqueue(string id, bool playNext = false)
    {
        SearchResultDto[]? results;
        try
        {
            results = await _gaida.GetFromJsonAsync<SearchResultDto[]>(
                $"/Audio/Search?query={Uri.EscapeDataString(id)}");
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
        {
            return;
        }

        var result = results?.FirstOrDefault();
        if (result is null) return;

        await _player.Enqueue(result.ToTrack(), playNext);
    }

    private Task HandleUpdateRoom(ReadOnlyMemory<char> value, User user)
    {
        var action = value.Span.Trim();
        var splitIndex = action.IndexOf(' ');
        if (splitIndex == -1 || splitIndex + 1 >= value.Length) return Task.CompletedTask;

        var parameterValue = action[splitIndex..];

        switch (action[..splitIndex])
        {
            case "name":
                RoomName = parameterValue.ToString();
                OnInfoModified?.Invoke();

                return user.SendAsync($"room name {RoomName}");

            case "description":
                RoomDescription = parameterValue.ToString();
                OnInfoModified?.Invoke();

                return user.SendAsync($"room description {RoomDescription}");

            default:
                return Task.CompletedTask;
        }
    }

    private Task HandleParameterlessMessages(ReadOnlyMemory<char> name, User user)
    {
        return name.Span switch
        {
            "end" => _player.SetFinished(user.Id),
            "next" => _player.Next(),
            "previous" => _player.Previous(),
            "playpause" => _player.TogglePlaying(),
            "stop" => _player.Stop(),
            "shuffle" => _player.Shuffle(),
            "clear" => _player.Clear(),
            "loaded" => _player.SetLoaded(user.Id),
            "sync" => SyncTo(user),
            _ => Task.CompletedTask
        };
    }

    private Task SyncTo(User user)
    {
        return _player.SyncTo(user);
    }
}
