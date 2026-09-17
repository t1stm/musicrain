using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace Selo.Multiplayer.Handlers;

public class UserStore
{
    /// <summary>
    ///     The live members. Concurrent because the broadcast path walks it on every frame while
    ///     connections are still joining and leaving — under the old dictionary-plus-semaphore that
    ///     walk was unsynchronised and a join landing mid-broadcast could tear the enumeration.
    /// </summary>
    public ConcurrentDictionary<string, User> Users { get; } = new();

    public int Count => Users.Count;

    public ValueTask<User> GetOrAddUser(string id, WebSocket webSocket, Func<User, Task>? onAdd = null)
    {
        // the common case is a member who is already here — every message after their first
        // takes this branch, which now costs neither a semaphore wait nor a Task
        if (Users.TryGetValue(id, out var user)) return new ValueTask<User>(user);

        user = new User { Id = id, WebSocket = webSocket };
        if (!Users.TryAdd(id, user))
            return new ValueTask<User>(Users.TryGetValue(id, out var raced) ? raced : user);

        var task = onAdd?.Invoke(user);
        return task is null ? new ValueTask<User>(user) : Added(task, user);
    }

    public Task RemoveUser(string id)
    {
        Users.TryRemove(id, out _);
        return Task.CompletedTask;
    }

    /// <returns>The user, or null when this id was never in the store.</returns>
    public User? GetUser(string id)
    {
        return Users.GetValueOrDefault(id);
    }

    private static async ValueTask<User> Added(Task task, User user)
    {
        await task;
        return user;
    }
}