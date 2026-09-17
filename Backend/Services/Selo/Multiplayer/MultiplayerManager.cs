using System.Collections.Concurrent;

namespace Selo.Multiplayer;

public class MultiplayerManager(HttpClient gaida)
{
    // concurrent so the room-list socket can serialise the collection while another request
    // creates a room, which the dictionary-plus-semaphore pair never actually guarded
    private readonly ConcurrentDictionary<Guid, Room> _rooms = new();

    /// <summary>Raised when the room list or any room's info changes.</summary>
    public event Func<Task>? RoomsChanged;

    public Task<Guid> CreateNewRoom()
    {
        var guid = Guid.NewGuid();

        _rooms.TryAdd(guid, new Room(guid, gaida)
        {
            OnInfoModified = () => RoomsChanged?.Invoke(),
            OnEmptied = () => RemoveRoom(guid)
        });

        RoomsChanged?.Invoke();
        return Task.FromResult(guid);
    }

    public Room? GetRoom(Guid roomId)
    {
        return _rooms.GetValueOrDefault(roomId);
    }

    public ICollection<Room> GetRooms()
    {
        return _rooms.Values;
    }

    /// <summary>
    ///     Drops a room and tells the room-list sockets. Idempotent: two members dropping at once
    ///     both see an empty store, and only the one that wins <c>TryRemove</c> announces it.
    /// </summary>
    public void RemoveRoom(Guid roomId)
    {
        if (!_rooms.TryRemove(roomId, out _)) return;

        RoomsChanged?.Invoke();
    }
}
