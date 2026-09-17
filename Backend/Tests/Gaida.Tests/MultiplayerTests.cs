using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Selo.Controllers.Helpers;
using Selo.Multiplayer;
using Selo.Multiplayer.Handlers;
using MultiplayerController = Selo.Controllers.Multiplayer;

namespace Gaida.Tests;

public class MultiplayerManagerTests
{
    [Fact]
    public async Task CreateNewRoomRegistersRoomAndRaisesChangeEvent()
    {
        var manager = new MultiplayerManager(new HttpClient());
        var changeCount = 0;
        manager.RoomsChanged += () =>
        {
            changeCount++;
            return Task.CompletedTask;
        };

        var roomId = await manager.CreateNewRoom();

        var room = Assert.IsType<Room>(manager.GetRoom(roomId));
        Assert.Equal(roomId, room.RoomId);
        Assert.Equal(roomId.ToString(), room.RoomName);
        Assert.Empty(room.RoomDescription);
        Assert.Equal([room], manager.GetRooms());
        Assert.Equal(1, changeCount);
    }

    [Fact]
    public async Task TheLastMemberLeavingTakesTheRoomWithThem()
    {
        var manager = new MultiplayerManager(new HttpClient());
        var roomId = await manager.CreateNewRoom();
        var room = Assert.IsType<Room>(manager.GetRoom(roomId));
        var changeCount = 0;
        manager.RoomsChanged += () =>
        {
            changeCount++;
            return Task.CompletedTask;
        };

        await room.GetOrAddUser("one", new RecordingWebSocket(), "One");
        await room.GetOrAddUser("two", new RecordingWebSocket(), "Two");

        await room.RemoveUser("one");
        Assert.Same(room, manager.GetRoom(roomId));
        Assert.Equal(0, changeCount);

        await room.RemoveUser("two");
        Assert.Null(manager.GetRoom(roomId));
        Assert.Empty(manager.GetRooms());
        Assert.Equal(1, changeCount);

        // the room is already gone; a second departure must not announce it again
        await room.RemoveUser("two");
        Assert.Equal(1, changeCount);
    }

    [Fact]
    public async Task ARoomNobodyEverJoinedStays()
    {
        var manager = new MultiplayerManager(new HttpClient());
        var roomId = await manager.CreateNewRoom();
        var room = Assert.IsType<Room>(manager.GetRoom(roomId));

        // a socket can close before it ever joins: there is no member to lose, so
        // this must not read as the room emptying out
        await room.RemoveUser("never here");

        Assert.Same(room, manager.GetRoom(roomId));
    }

    [Fact]
    public async Task UpdatingRoomInfoRaisesManagerChangeEvent()
    {
        var manager = new MultiplayerManager(new HttpClient());
        var roomId = await manager.CreateNewRoom();
        var room = Assert.IsType<Room>(manager.GetRoom(roomId));
        var socket = new RecordingWebSocket();
        var user = new User { Id = "host", Username = "Host", WebSocket = socket };
        var changeCount = 0;
        manager.RoomsChanged += () =>
        {
            changeCount++;
            return Task.CompletedTask;
        };

        await room.HandleUserMessage(user, "updateroom name Listening Party");

        Assert.Equal("Listening Party", room.RoomName.Trim());
        Assert.Equal(1, changeCount);
        Assert.EndsWith("Listening Party", Assert.Single(socket.Messages));
    }
}

public class MultiplayerUserTests
{
    [Fact]
    public async Task UserStoreReturnsExistingUserAndRunsOnAddOnlyOnce()
    {
        var store = new UserStore();
        var firstSocket = new RecordingWebSocket();
        var secondSocket = new RecordingWebSocket();
        var addCount = 0;

        var first = await store.GetOrAddUser("connection:1", firstSocket, _ =>
        {
            addCount++;
            return Task.CompletedTask;
        });
        var second = await store.GetOrAddUser("connection:1", secondSocket, _ =>
        {
            addCount++;
            return Task.CompletedTask;
        });

        Assert.Same(first, second);
        Assert.Same(firstSocket, second.WebSocket);
        Assert.Equal(1, store.Count);
        Assert.Equal(1, addCount);
    }

    [Fact]
    public async Task MessageQueueBroadcastsOnlyToOpenUsers()
    {
        var store = new UserStore();
        var openSocket = new RecordingWebSocket();
        var closedSocket = new RecordingWebSocket(WebSocketState.Closed);
        await store.GetOrAddUser("open", openSocket);
        await store.GetOrAddUser("closed", closedSocket);
        var queue = new MessageQueue(store);

        await queue.Add("playing True");

        Assert.Equal(["playing True"], openSocket.Messages);
        Assert.Empty(closedSocket.Messages);
    }

    /// <summary>
    ///     The broadcast walks the store one socket at a time, so a peer that dropped between the state check
    ///     and the send used to throw straight out of the loop and take the frame away from everybody after it
    ///     — one bad connection leaving the rest of the room on a stale position.
    /// </summary>
    [Fact]
    public async Task MessageQueueKeepsBroadcastingPastASocketThatThrows()
    {
        var store = new UserStore();
        var failing = new RecordingWebSocket(sendFails: true);
        var healthy = new RecordingWebSocket();
        await store.GetOrAddUser("failing", failing);
        await store.GetOrAddUser("healthy", healthy);
        var queue = new MessageQueue(store);

        await queue.Add("seek 12 1700000000000");

        Assert.Equal(["seek 12 1700000000000"], healthy.Messages);
    }

    [Fact]
    public void ChatUsernameUsesExplicitNameOrStableAnonymousName()
    {
        var socket = new RecordingWebSocket();
        var named = new User { Id = "one:trace", Username = "Ada", WebSocket = socket };
        var anonymous = new User { Id = "two:trace", WebSocket = socket };

        Assert.Equal("Ada", named.ChatUsername);
        Assert.Equal("Anonymous two", anonymous.ChatUsername);
        Assert.Equal(anonymous.ChatUsername, anonymous.ChatUsername);
    }
}

public class VirtualPlayerTests
{
    [Fact]
    public async Task JoinedUserReceivesQueuePlaybackStateAndAnnouncement()
    {
        var (player, store) = TestObjects.Player();
        var socket = new RecordingWebSocket();
        var user = await store.GetOrAddUser("listener:1", socket);
        player.Items.Add(TestObjects.Result("audio://one"));

        await player.Joined(user);

        Assert.Collection(socket.Messages,
            queue =>
            {
                Assert.StartsWith("queue ", queue);
                Assert.Contains("audio://one", queue);
            },
            current => Assert.Equal("current 0", current),
            playing => Assert.Equal("playing True", playing),
            seek => Assert.Equal("seek 0", TestObjects.Unstamped(seek)),
            chat => Assert.Equal("chat System %% User 'Anonymous listener' joined the session.", chat));
    }

    [Fact]
    public async Task QueueCommandsReorderNavigateAndRejectInvalidIndexes()
    {
        var (player, store) = TestObjects.Player();
        var socket = new RecordingWebSocket();
        await store.GetOrAddUser("listener", socket);
        var first = TestObjects.Result("audio://first");
        var second = TestObjects.Result("audio://second");
        var third = TestObjects.Result("audio://third");
        player.Items.AddRange([first, second, third]);

        await player.SkipTo(1);

        Assert.Equal(["playing False", "current 1"], socket.Messages);
        socket.ClearMessages();

        await player.SetNext(0);

        Assert.Equal([second, first, third], player.Items);
        // the move carried a track across the current one, so the index leads the list
        Assert.Equal(2, socket.Messages.Count);
        Assert.Equal("index 0", socket.Messages[0]);
        Assert.StartsWith("queue ", socket.Messages[1]);
        socket.ClearMessages();

        await player.Remove(1);
        await player.SkipTo(-1);
        await player.SetNext(20);

        Assert.Equal([second, third], player.Items);
        Assert.Single(socket.Messages);
        Assert.StartsWith("queue ", socket.Messages[0]);
    }

    [Fact]
    public async Task ShuffleLeadsWithWhatIsPlayingAndKeepsPlayingIt()
    {
        var (player, store) = TestObjects.Player();
        var socket = new RecordingWebSocket();
        await store.GetOrAddUser("listener", socket);
        var first = TestObjects.Result("audio://first");
        var second = TestObjects.Result("audio://second");
        var third = TestObjects.Result("audio://third");
        player.Items.AddRange([first, second, third]);

        await player.SkipTo(2);
        socket.ClearMessages();

        await player.Shuffle();

        // the current track leads, everything else is behind it, and nothing about
        // playback moved — no `current`, so no barrier
        Assert.Same(third, player.Items[0]);
        Assert.Equal([first, second, third], player.Items.OrderBy(item => item.Id));
        Assert.Equal(2, socket.Messages.Count);
        Assert.Equal("index 0", socket.Messages[0]);
        Assert.StartsWith("queue ", socket.Messages[1]);
    }

    [Fact]
    public async Task ClearKeepsWhatIsPlayingAndDropsTheRest()
    {
        var (player, store) = TestObjects.Player();
        var socket = new RecordingWebSocket();
        await store.GetOrAddUser("listener", socket);
        var first = TestObjects.Result("audio://first");
        var second = TestObjects.Result("audio://second");
        player.Items.AddRange([first, second, TestObjects.Result("audio://third")]);

        await player.SkipTo(1);
        socket.ClearMessages();

        await player.Clear();

        Assert.Equal([second], player.Items);
        Assert.Equal("index 0", socket.Messages[0]);
        Assert.StartsWith("queue ", socket.Messages[1]);
    }

    [Fact]
    public async Task MoveLandsTheTrackWhereItWasDroppedAndCarriesTheIndex()
    {
        var (player, store) = TestObjects.Player();
        var socket = new RecordingWebSocket();
        await store.GetOrAddUser("listener", socket);
        var first = TestObjects.Result("audio://first");
        var second = TestObjects.Result("audio://second");
        var third = TestObjects.Result("audio://third");
        player.Items.AddRange([first, second, third]);

        await player.SkipTo(1);
        socket.ClearMessages();

        // an upcoming track dragged above the current one: the current track keeps
        // playing, one slot further down
        await player.Move(2, 0);

        Assert.Equal([third, first, second], player.Items);
        Assert.Equal("index 2", socket.Messages[0]);
        socket.ClearMessages();

        // the current track itself, dragged to the front
        await player.Move(2, 0);
        Assert.Equal([second, third, first], player.Items);
        Assert.Equal("index 0", socket.Messages[0]);
        socket.ClearMessages();

        await player.Move(0, 0);
        await player.Move(-1, 2);
        await player.Move(0, 9);

        Assert.Equal([second, third, first], player.Items);
        Assert.Empty(socket.Messages);
    }

    [Fact]
    public async Task PlayNextPutsTheTrackAfterTheCurrentOne()
    {
        var (player, store) = TestObjects.Player();
        var socket = new RecordingWebSocket();
        await store.GetOrAddUser("listener", socket);
        var first = TestObjects.Result("audio://first");
        var last = TestObjects.Result("audio://last");
        player.Items.AddRange([first, last]);
        await player.SetLoaded("listener");
        socket.ClearMessages();

        var added = TestObjects.Result("audio://added");
        await player.Enqueue(added, true);

        Assert.Equal([first, added, last], player.Items);
        Assert.Single(socket.Messages);
        Assert.StartsWith("queue ", socket.Messages[0]);
    }

    [Fact]
    public async Task PlayNextOnAnIdleRoomStartsTheTrackLikeAnyOtherAdd()
    {
        var (player, store) = TestObjects.Player();
        var socket = new RecordingWebSocket();
        await store.GetOrAddUser("only", socket);
        await player.SetLoaded("only");
        socket.ClearMessages();

        await player.Enqueue(TestObjects.Result("audio://one"), true);

        Assert.Contains("current 0", socket.Messages);
        Assert.Contains("playing False", socket.Messages);
    }

    [Fact]
    public async Task AnIdleRoomDoesNotStartItsClock()
    {
        var (player, store) = TestObjects.Player();
        var socket = new RecordingWebSocket();
        await store.GetOrAddUser("only", socket);

        // joining an empty room answers a `current` frame with nothing to load
        await player.SetLoaded("only");

        Assert.Equal(0, await player.GetCurrentTime());
        Assert.DoesNotContain("playing True", socket.Messages);
    }

    [Fact]
    public async Task TheFirstItemAddedToAnIdleRoomGoesThroughTheBarrier()
    {
        var (player, store) = TestObjects.Player();
        var socket = new RecordingWebSocket();
        await store.GetOrAddUser("only", socket);
        await player.SetLoaded("only");
        socket.ClearMessages();

        await player.Enqueue(TestObjects.Result("audio://one"));

        // without this the room kept whatever clock it had and the track started
        // however many seconds into itself
        Assert.Equal(0, await player.GetCurrentTime());
        Assert.Contains("current 0", socket.Messages);
        Assert.Contains("playing False", socket.Messages);

        await player.SetLoaded("only");
        Assert.Contains("seek 0", TestObjects.Unstamped(socket.Messages));
        Assert.Contains("playing True", socket.Messages);
    }

    [Fact]
    public async Task AddingToARoomThatIsAlreadyPlayingLeavesItAlone()
    {
        var (player, store) = TestObjects.Player();
        var socket = new RecordingWebSocket();
        await store.GetOrAddUser("only", socket);
        await player.Enqueue(TestObjects.Result("audio://one"));
        await player.SetLoaded("only");
        socket.ClearMessages();

        await player.Enqueue(TestObjects.Result("audio://two"));

        // the queue grew; nothing about what is playing changed
        Assert.Single(socket.Messages);
        Assert.StartsWith("queue ", socket.Messages[0]);
    }

    [Fact]
    public async Task AnEmptyRoomReleasesNeitherBarrier()
    {
        var (player, store) = TestObjects.Player();
        await store.GetOrAddUser("only", new RecordingWebSocket());
        player.Items.AddRange([TestObjects.Result("audio://one"), TestObjects.Result("audio://two")]);
        await player.Next();

        // exactly what Room.RemoveUser runs once the last user is gone: with a
        // count of zero, `0 < 0` used to read as "everybody reported", so the
        // room rewound itself, force-played, and advanced a track on the way out
        await store.RemoveUser("only");
        await player.UserLeft("only");

        var joiner = new RecordingWebSocket();
        await store.GetOrAddUser("next", joiner, player.Joined);

        Assert.Contains("current 1", joiner.Messages);
        Assert.DoesNotContain("current 2", joiner.Messages);
        Assert.Contains("playing False", joiner.Messages);
        Assert.DoesNotContain("playing True", joiner.Messages);
    }

    [Fact]
    public async Task ADepartureNeverRewindsARoomThatIsAlreadyPlaying()
    {
        var (player, store) = TestObjects.Player();
        var staying = new RecordingWebSocket();
        await store.GetOrAddUser("staying", staying);
        await player.Enqueue(TestObjects.Result("audio://one"));
        await player.SetLoaded("staying");

        // Somebody joins mid-track and answers the `current` frame `Joined` replayed at
        // them. No barrier is armed for a join, so that vote must not be kept.
        await store.GetOrAddUser("leaving", new RecordingWebSocket());
        await player.SetLoaded("leaving");
        staying.ClearMessages();

        // ...because when they drop, one vote against one remaining member used to add
        // up, and the room rewound to zero and force-played under everybody left in it.
        await store.RemoveUser("leaving");
        await player.UserLeft("leaving");

        Assert.Empty(staying.Messages);
        Assert.True(await player.GetCurrentTime() > 0);
    }

    [Fact]
    public async Task ADepartedUsersVoteLeavesWithThem()
    {
        var (player, store) = TestObjects.Player();
        var staying = new RecordingWebSocket();
        await store.GetOrAddUser("staying", staying);
        await store.GetOrAddUser("leaving", new RecordingWebSocket());
        player.Items.AddRange([TestObjects.Result("audio://one"), TestObjects.Result("audio://two")]);
        await player.Next();
        staying.ClearMessages();

        // one of the two played the track out; the other is still on it
        await player.SetFinished("leaving");
        await store.RemoveUser("leaving");
        await player.UserLeft("leaving");

        // their `end` used to stay on the tally and skip the track under the one left
        Assert.DoesNotContain("current 2", staying.Messages);
    }

    [Fact]
    public async Task LoadedAndFinishedWaitForEveryConnectedUser()
    {
        var (player, store) = TestObjects.Player();
        var firstSocket = new RecordingWebSocket();
        var secondSocket = new RecordingWebSocket();
        await store.GetOrAddUser("one", firstSocket);
        await store.GetOrAddUser("two", secondSocket);
        player.Items.AddRange([TestObjects.Result("audio://one"), TestObjects.Result("audio://two")]);

        await player.SetLoaded("one");
        Assert.Empty(firstSocket.Messages);
        Assert.Empty(secondSocket.Messages);

        // the same member reporting twice is not a quorum
        await player.SetLoaded("one");
        Assert.Empty(firstSocket.Messages);

        await player.SetLoaded("two");
        Assert.Equal(["seek 0", "playing True"], TestObjects.Unstamped(firstSocket.Messages));
        Assert.Equal(["seek 0", "playing True"], TestObjects.Unstamped(secondSocket.Messages));
        firstSocket.ClearMessages();
        secondSocket.ClearMessages();

        await player.SetFinished("one");
        Assert.Empty(firstSocket.Messages);
        await player.SetFinished("two");

        Assert.Equal(["playing False", "current 1"], firstSocket.Messages);
        Assert.Equal(firstSocket.Messages, secondSocket.Messages);
    }

    [Fact]
    public async Task SeekingPastAQuarterOfAnHourStaysOnTheClock()
    {
        var (player, store) = TestObjects.Player();
        var socket = new RecordingWebSocket();
        await store.GetOrAddUser("listener", socket);

        // 922 seconds is where `Ticks * Stopwatch.Frequency` used to overflow a
        // long, and the library has half-hour mixes in it, so this is an ordinary
        // position in an ordinary track rather than an edge case
        await player.SeekTo(1800);

        Assert.InRange(SeekSeconds(Assert.Single(socket.Messages)), 1800, 1801);
        Assert.InRange(await player.GetCurrentTime(), 1800, 1801);

        // the same arithmetic carries the position out of a pause and back in
        await player.TogglePlaying();
        await player.TogglePlaying();

        Assert.InRange(await player.GetCurrentTime(), 1800, 1802);
    }

    [Fact]
    public async Task SeekPauseResumeAndStopBroadcastPlaybackState()
    {
        var (player, store) = TestObjects.Player();
        var socket = new RecordingWebSocket();
        await store.GetOrAddUser("listener", socket);

        await player.SeekTo(12.5);
        Assert.InRange(SeekSeconds(Assert.Single(socket.Messages)), 12.4, 12.7);
        socket.ClearMessages();

        // the position leads the state change on both edges: a client has to land on
        // it before it starts moving, and resuming used to send no position at all
        await player.TogglePlaying();
        Assert.InRange(SeekSeconds(socket.Messages[0]), 12.4, 12.7);
        Assert.Equal("playing False", socket.Messages[1]);
        socket.ClearMessages();

        await player.TogglePlaying();
        Assert.InRange(SeekSeconds(socket.Messages[0]), 12.4, 12.7);
        Assert.Equal("playing True", socket.Messages[1]);
        socket.ClearMessages();

        await player.Stop();
        Assert.Equal(["stop"], socket.Messages);
    }

    /// <summary>
    ///     Every socket runs its own read loop, so the clock is written by as many threads as there are
    ///     listeners. Two things broke under that: <c>LoadedCount++</c> lost increments and left the loading
    ///     barrier permanently short of the member count, and the position was read through a
    ///     <c>StartTime.HasValue</c> check taken outside the lock — a concurrent track change could null it
    ///     before the <c>.Value</c>, throwing past a <c>Release()</c> that had no <c>finally</c> and stranding
    ///     the room's semaphore for the life of the process.
    /// </summary>
    [Fact]
    public async Task ConcurrentClockChangesNeverStrandTheRoomLock()
    {
        const int users = 4;
        var (player, store) = TestObjects.Player();
        var sockets = new RecordingWebSocket[users];

        for (var index = 0; index < users; index++)
        {
            sockets[index] = new RecordingWebSocket();
            await store.GetOrAddUser($"listener:{index}", sockets[index]);
        }

        player.Items.Add(TestObjects.Result("audio://one"));

        // Everybody reports `loaded` at once: lose one increment and the barrier never releases.
        await Task.WhenAll(Enumerable.Range(0, users)
            .Select(index => Task.Run(() => player.SetLoaded($"listener:{index}"))));
        Assert.Contains("seek 0", TestObjects.Unstamped(sockets[0].Messages));

        // The clock is running now, so the readers race every path that nulls StartTime.
        var first = store.GetUser("listener:0")!;
        var hammer = new List<Task>();

        for (var round = 0; round < 64; round++)
        {
            hammer.Add(Task.Run(() => player.SeekTo(30)));
            hammer.Add(Task.Run(() => player.Next()));
            hammer.Add(Task.Run(() => player.TogglePlaying()));
            hammer.Add(Task.Run(() => player.SyncTo(first)));
            hammer.Add(Task.Run(() => player.GetCurrentTime()));
            hammer.Add(Task.Run(() => player.SetLoaded("listener:0")));
        }

        await Task.WhenAll(hammer).WaitAsync(TimeSpan.FromSeconds(30));

        // A stranded semaphore shows up here: this one would never come back.
        var position = await player.GetCurrentTime().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(position >= 0, $"position went backwards: {position}");
    }

    /// <summary>The position out of a `seek &lt;seconds&gt; &lt;stamp&gt;` frame.</summary>
    private static double SeekSeconds(string frame)
    {
        return double.Parse(TestObjects.Unstamped(frame)["seek ".Length..]);
    }
}

public class MultiplayerRoomTests
{
    [Fact]
    public async Task RoomRoutesChatSyncAndInfoCommands()
    {
        var room = new Room(Guid.NewGuid(), new HttpClient());
        var socket = new RecordingWebSocket();
        var user = await room.GetOrAddUser("host:trace", socket, "Ada");
        socket.ClearMessages();

        await room.HandleUserMessage(user, "chat Hello room");
        Assert.StartsWith("chat Ada %%", Assert.Single(socket.Messages));
        Assert.EndsWith("Hello room", socket.Messages[0]);
        socket.ClearMessages();

        await room.HandleUserMessage(user, "sync");
        Assert.Equal(["sync 0"], TestObjects.Unstamped(socket.Messages));
        socket.ClearMessages();

        await room.HandleUserMessage(user, "updateroom description Shared favorites");
        Assert.Equal("Shared favorites", room.RoomDescription.Trim());
        Assert.EndsWith("Shared favorites", Assert.Single(socket.Messages));
    }

    [Fact]
    public async Task RemovingAUserWhoNeverJoinedIsHarmless()
    {
        var room = new Room(Guid.NewGuid(), new HttpClient());
        var socket = new RecordingWebSocket();
        await room.GetOrAddUser("present", socket, "Present User");
        socket.ClearMessages();

        await room.RemoveUser("never here");

        // nothing announced, and the store's semaphore is still usable after
        Assert.Empty(socket.Messages);
        Assert.Equal("Present User", (await room.GetOrAddUser("present", socket, null)).ChatUsername);
    }

    [Fact]
    public async Task RemovingUserBroadcastsDepartureToRemainingUsers()
    {
        var room = new Room(Guid.NewGuid(), new HttpClient());
        var leavingSocket = new RecordingWebSocket();
        var remainingSocket = new RecordingWebSocket();
        await room.GetOrAddUser("leaving", leavingSocket, "Leaving User");
        await room.GetOrAddUser("remaining", remainingSocket, "Remaining User");
        leavingSocket.ClearMessages();
        remainingSocket.ClearMessages();

        await room.RemoveUser("leaving");

        Assert.Empty(leavingSocket.Messages);
        Assert.Equal(["chat System %% User 'Leaving User' left from the session."], remainingSocket.Messages);
    }
}

public class WebSocketTextReaderTests
{
    [Fact]
    public async Task AConnectionThatGoesAwayReadsAsTheEndOfTheSession()
    {
        var reader = new WebSocketTextReader();

        Assert.Null(await reader.ReadWholeMessageAsync(
            new RecordingWebSocket(receiveFailure: new WebSocketException("connection reset"))));
        Assert.Null(await reader.ReadWholeMessageAsync(
            new RecordingWebSocket(receiveFailure: new OperationCanceledException())));
    }

    [Fact]
    public async Task AFaultThatIsNotADisconnectIsNotSwallowed()
    {
        var reader = new WebSocketTextReader();
        var socket = new RecordingWebSocket(receiveFailure: new InvalidOperationException("a real bug"));

        // this used to read as a clean close, ending the session with no trace
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadWholeMessageAsync(socket));
    }
}

public class MultiplayerControllerTests
{
    [Fact]
    public async Task CreateRoomReturnsTheRegisteredRoom()
    {
        var manager = new MultiplayerManager(new HttpClient());
        var controller = Controller(manager);

        var result = await controller.CreateRoom();

        var json = Assert.IsType<JsonResult>(result);
        var room = Assert.IsType<Room>(json.Value);
        Assert.Same(room, manager.GetRoom(room.RoomId));
    }

    [Fact]
    public async Task WebSocketEndpointsRejectRegularHttpRequests()
    {
        var controller = Controller(new MultiplayerManager(new HttpClient()));

        Assert.IsType<BadRequestResult>(await controller.Rooms());
        Assert.IsType<BadRequestResult>(await controller.Join(Guid.NewGuid().ToString(), null));
    }

    [Fact]
    public async Task AJoinThatFaultsStillTakesTheUserBackOutOfTheRoom()
    {
        var manager = new MultiplayerManager(new HttpClient());
        var roomId = await manager.CreateNewRoom();
        var room = manager.GetRoom(roomId)!;

        var staying = new RecordingWebSocket();
        await room.GetOrAddUser("staying", staying, "Staying User");
        staying.ClearMessages();

        // the join handshake adds the user, then sends — and this one cannot send
        var controller = Controller(manager, new RecordingWebSocket(sendFails: true));
        await Assert.ThrowsAsync<WebSocketException>(() => controller.Join(roomId.ToString(), "Vanishing User"));

        // without the finally the user stayed in the store, and every barrier
        // after that waited on a member who could never answer
        Assert.Contains(staying.Messages, message => message.Contains("'Vanishing User' left"));
    }

    [Fact]
    public async Task AJoinWhoseReceiveFailsStillTakesTheUserBackOutOfTheRoom()
    {
        var manager = new MultiplayerManager(new HttpClient());
        var roomId = await manager.CreateNewRoom();
        var room = manager.GetRoom(roomId)!;

        var staying = new RecordingWebSocket();
        await room.GetOrAddUser("staying", staying, "Staying User");
        staying.ClearMessages();

        var controller = Controller(manager,
            new RecordingWebSocket(receiveFailure: new WebSocketException("connection reset")));

        await controller.Join(roomId.ToString(), "Vanishing User");

        Assert.Contains(staying.Messages, message => message.Contains("'Vanishing User' left"));
    }

    private static MultiplayerController Controller(MultiplayerManager manager, WebSocket? socket = null)
    {
        var context = new DefaultHttpContext();
        if (socket is not null) context.Features.Set<IHttpWebSocketFeature>(new FakeWebSocketFeature(socket));

        return new MultiplayerController(NullLogger<MultiplayerController>.Instance, manager)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }

    private sealed class FakeWebSocketFeature(WebSocket socket) : IHttpWebSocketFeature
    {
        public bool IsWebSocketRequest => true;

        public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context)
        {
            return Task.FromResult(socket);
        }
    }
}

internal static class TestObjects
{
    public static (VirtualPlayer Player, UserStore Store) Player()
    {
        var store = new UserStore();
        return (new VirtualPlayer(new MessageQueue(store)), store);
    }

    /// <summary>
    ///     The frame with its trailing stamp checked and stripped, so the assertions below
    ///     can stay about the position rather than about a wall clock.
    /// </summary>
    public static string Unstamped(string frame)
    {
        var split = frame.LastIndexOf(' ');
        Assert.True(split != -1 && long.TryParse(frame[(split + 1)..], out _), $"no stamp on '{frame}'");

        var stamp = long.Parse(frame[(split + 1)..]);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Assert.InRange(stamp, now - 60_000, now);

        return frame[..split];
    }

    /// <summary>
    ///     The same, over a whole transcript: only the frames that carry a position
    ///     are stamped, so the rest have to pass through untouched.
    /// </summary>
    public static IEnumerable<string> Unstamped(IEnumerable<string> frames)
    {
        return frames.Select(frame =>
            frame.StartsWith("seek ") || frame.StartsWith("sync ") ? Unstamped(frame) : frame);
    }

    public static TrackDto Result(string id)
    {
        return new TrackDto { Id = id, Name = id };
    }
}

internal sealed class RecordingWebSocket(
    WebSocketState initialState = WebSocketState.Open,
    bool sendFails = false,
    Exception? receiveFailure = null) : WebSocket
{
    private readonly ConcurrentQueue<string> _messages = new();
    private WebSocketCloseStatus? _closeStatus;
    private string? _closeStatusDescription;
    private WebSocketState _state = initialState;

    public IReadOnlyList<string> Messages => _messages.ToArray();
    public override WebSocketCloseStatus? CloseStatus => _closeStatus;
    public override string? CloseStatusDescription => _closeStatusDescription;
    public override WebSocketState State => _state;
    public override string? SubProtocol => null;

    public void ClearMessages()
    {
        _messages.Clear();
    }

    public override void Abort()
    {
        _state = WebSocketState.Aborted;
    }

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription,
        CancellationToken cancellationToken)
    {
        _closeStatus = closeStatus;
        _closeStatusDescription = statusDescription;
        _state = WebSocketState.Closed;
        return Task.CompletedTask;
    }

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription,
        CancellationToken cancellationToken)
    {
        _closeStatus = closeStatus;
        _closeStatusDescription = statusDescription;
        _state = WebSocketState.CloseSent;
        return Task.CompletedTask;
    }

    public override void Dispose()
    {
        _state = WebSocketState.Closed;
    }

    public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer,
        CancellationToken cancellationToken)
    {
        throw receiveFailure ?? new NotSupportedException();
    }

    public override ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        throw receiveFailure ?? new NotSupportedException();
    }

    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage,
        CancellationToken cancellationToken)
    {
        if (sendFails) throw new WebSocketException("the peer is already gone");
        _messages.Enqueue(Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count));
        return Task.CompletedTask;
    }

    public override ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType,
        bool endOfMessage, CancellationToken cancellationToken)
    {
        if (sendFails) throw new WebSocketException("the peer is already gone");
        _messages.Enqueue(Encoding.UTF8.GetString(buffer.Span));
        return ValueTask.CompletedTask;
    }
}