using System.Net.WebSockets;
using Selo.Multiplayer.Handlers;

namespace Selo.Multiplayer;

public static class SelfCheck
{
    public static async Task RunAsync()
    {
        var store = new UserStore();
        var user1 = await store.GetOrAddUser("user-1", new ClientWebSocket());
        var user2 = await store.GetOrAddUser("user-2", new ClientWebSocket());

        var queue = new MessageQueue(store);
        var player = new VirtualPlayer(queue);

        await player.Enqueue(new TrackDto { Id = "audio://self-check" });

        Assert(await player.GetCurrentTime() == 0, "clock must be at 0 before anyone has loaded");

        await player.SetLoaded(user1.Id);
        Assert(await player.GetCurrentTime() == 0, "clock must still be at 0 with only 1 of 2 members loaded");

        await player.SetLoaded(user2.Id);
        await Task.Delay(20);
        Assert(await player.GetCurrentTime() > 0, "clock must start once every member has loaded");

        Console.WriteLine("SelfCheck: loading barrier OK");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"SelfCheck failed: {message}");
    }
}