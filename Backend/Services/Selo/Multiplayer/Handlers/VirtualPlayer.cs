using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using JetBrains.Annotations;

namespace Selo.Multiplayer.Handlers;

public class VirtualPlayer(MessageQueue messageQueue)
{
    /// <summary>Who reported the current item as played out.</summary>
    private readonly HashSet<string> _finished = [];

    /// <summary>Who reported the current item as buffered.</summary>
    private readonly HashSet<string> _loaded = [];

    /// <summary>
    ///     Guards every read and write of the room clock, the queue and the two barrier counters, and is held
    ///     across the broadcast so state changes and the frames announcing them leave in the same order. Each
    ///     socket runs its own read loop, so without this the clock is mutated by as many threads as there are
    ///     listeners: <c>StartTime</c> could go null between a <c>HasValue</c> check and the <c>.Value</c> that
    ///     followed it, and <c>Loaded</c> could lose a vote and strand the loading barrier.
    /// </summary>
    /// <remarks>
    ///     ponytail: one lock for the whole room. Public methods take it, <c>*Core</c> helpers assume it is
    ///     already held — SemaphoreSlim is not reentrant, so calling a public one from inside another deadlocks.
    ///     A room is a handful of listeners and MessageQueue already serialises the sends underneath, so this
    ///     costs nothing that was not already serial. Split it per-field if rooms grow past a few dozen members.
    /// </remarks>
    private readonly SemaphoreSlim _sync = new(1);

    private int _currentIndex;

    /// <summary>
    ///     Whether the room is still waiting on everyone to buffer the current track. Only an armed
    ///     barrier may release the clock: a client answers the <c>current</c> frame it gets on the way
    ///     in, and the room does not arm a barrier for a join. That vote used to stand, and the next
    ///     departure made the tally add up — rewinding a mid-track room to zero for everyone left.
    /// </summary>
    private bool _loading = true;

    private TimeSpan? _pauseTime;
    private bool _playing = true;

    private long? _startTime;
    public List<TrackDto> Items { get; private set; } = [];

    public Task Next()
    {
        return LockedAsync(NextCore);
    }

    public Task Previous()
    {
        return LockedAsync(async () =>
        {
            if (_currentIndex > 0)
                _currentIndex--;

            UpdateStart();
            await SetPlayingCore(false);
            await Broadcast($"current {_currentIndex}");
        });
    }

    public Task Remove(int index)
    {
        return LockedAsync(async () =>
        {
            if (index < 0 || index >= Items.Count) return;
            Items.RemoveAt(index);

            await ReindexCore(_currentIndex > index ? _currentIndex - 1 : _currentIndex);
        });
    }

    public Task SetNext(int index)
    {
        return LockedAsync(async () =>
        {
            if (index < 0 || index >= Items.Count || index == _currentIndex) return;

            var item = Items[index];
            Items.RemoveAt(index);

            var target = index < _currentIndex ? _currentIndex - 1 : _currentIndex;
            Items.Insert(target + 1, item);

            await ReindexCore(target);
        });
    }

    public Task SkipTo(int index)
    {
        return LockedAsync(async () =>
        {
            if (index < 0 || index >= Items.Count || index == _currentIndex) return;
            _currentIndex = index;

            UpdateStart();
            await SetPlayingCore(false);
            await Broadcast($"current {_currentIndex}");
        });
    }

    public Task SetFinished(string id)
    {
        return LockedAsync(async () =>
        {
            _finished.Add(id);
            await HandleFinishedCore();
        });
    }


    public Task Shuffle()
    {
        return LockedAsync(async () =>
        {
            // The current track leads and the rest is shuffled behind it. Shuffling the whole
            // list moved the item out from under `CurrentIndex` without touching the index, so
            // the room went on playing a track the queue no longer named there.
            var playing = CurrentItemCore();
            if (playing is not null) Items.RemoveAt(_currentIndex);

            Random.Shared.Shuffle(CollectionsMarshal.AsSpan(Items));

            if (playing is null)
            {
                await Broadcast(QueueMessage());
                return;
            }

            Items.Insert(0, playing);
            await ReindexCore(0);
        });
    }

    /// <summary>
    ///     The Clear button: everything goes except what is playing, which keeps playing. Nothing
    ///     touches the clock, so this is a queue edit and not a track change.
    /// </summary>
    public Task Clear()
    {
        return LockedAsync(async () =>
        {
            var playing = CurrentItemCore();
            Items = playing is null ? [] : [playing];

            await ReindexCore(0);
        });
    }

    /// <summary>
    ///     Drag-reorder: the track lands where it was dropped. <see cref="SetNext" /> is the only other
    ///     way to reorder and it can only pull a track to the front of the upcoming ones, so every drag
    ///     used to land in the same place regardless of where it was let go.
    /// </summary>
    public Task Move(int from, int to)
    {
        return LockedAsync(async () =>
        {
            if (from == to || from < 0 || from >= Items.Count || to < 0 || to >= Items.Count) return;

            var item = Items[from];
            Items.RemoveAt(from);
            Items.Insert(to, item);

            // whatever is playing keeps playing: only its index moves, and only when the
            // track was carried across it
            var index = _currentIndex;
            if (from == _currentIndex) index = to;
            else if (from < _currentIndex && to >= _currentIndex) index--;
            else if (from > _currentIndex && to <= _currentIndex) index++;

            await ReindexCore(index);
        });
    }

    public Task TogglePlaying()
    {
        return LockedAsync(async () =>
        {
            if (!_startTime.HasValue) return;

            _playing = !_playing;

            switch (_playing)
            {
                case false:
                    _pauseTime = Stopwatch.GetElapsedTime(_startTime.Value);
                    break;
                case true:
                    if (_pauseTime.HasValue)
                        _startTime = Stopwatch.GetTimestamp() - TimeSpanToTimestamp(_pauseTime.Value);
                    _pauseTime = null;
                    break;
            }

            // Both edges carry the position now, and the position goes out before the
            // state change so a client lands on it before it starts moving. Resuming
            // used to broadcast `playing True` alone, leaving every client to rediscover
            // where the room came back at from the next `sync` — which is a whole round
            // trip of being in the wrong place, on the one transition where everybody
            // is listening for it.
            await Broadcast($"seek {Stopwatch.GetElapsedTime(_startTime.Value).TotalSeconds} {Stamp()}");
            await SetPlayingCore(_playing);
        });
    }

    public Task Stop()
    {
        return LockedAsync(async () =>
        {
            _playing = false;
            _pauseTime = null;
            await messageQueue.Add("stop");
        });
    }

    /// <summary>
    ///     Appends a track, or drops it in right after the current one when <paramref name="playNext" />
    ///     is set. "Play next" used to be an <c>add</c> like any other, which put the track at the end of
    ///     the queue — the one place it was asked not to go.
    /// </summary>
    public Task Enqueue(TrackDto result, bool playNext = false)
    {
        return LockedAsync(async () =>
        {
            // current sits past the end of the queue exactly when nothing is playing,
            // so the item going in is the one that becomes current
            var startsPlayback = _currentIndex >= Items.Count;

            if (playNext && !startsPlayback)
                Items.Insert(_currentIndex + 1, result);
            else
                Items.Add(result);

            await Broadcast(QueueMessage());
            if (!startsPlayback) return;

            // That is a track change like any other and has to go through the loading
            // barrier. Without it the room plays its first track against whatever the
            // clock already read, so a song added to an idle room starts however many
            // seconds into itself.
            UpdateStart();
            await SetPlayingCore(false);
            await Broadcast($"current {_currentIndex}");
        });
    }

    public Task Joined(User user)
    {
        return LockedAsync(async () =>
        {
            await user.SendAsync(QueueMessage());
            await user.SendAsync($"current {_currentIndex}");
            await user.SendAsync($"playing {_playing}");

            if (Items.Count > 0)
                await user.SendAsync($"seek {CurrentTimeCore()} {Stamp()}");

            await Broadcast($"chat System %% User '{user.ChatUsername}' joined the session.");
        });
    }

    public Task SeekTo(double seconds)
    {
        return LockedAsync(async () =>
        {
            _startTime = Stopwatch.GetTimestamp() - TimeSpanToTimestamp(TimeSpan.FromSeconds(seconds));
            await Broadcast($"seek {Stopwatch.GetElapsedTime(_startTime.Value).TotalSeconds} {Stamp()}");
        });
    }

    public Task SetLoaded(string id)
    {
        return LockedAsync(async () =>
        {
            _loaded.Add(id);
            await HandleLoadedCore();
        });
    }

    /// <summary>
    ///     A member is gone. Both barriers count against the live member list, so a departure moves
    ///     the target and has to be re-checked or the room never advances past this track again. Their
    ///     own votes go with them: a tally that keeps counting someone who has left is a barrier that
    ///     releases for a room that never all agreed.
    /// </summary>
    public Task UserLeft(string id)
    {
        return LockedAsync(async () =>
        {
            _loaded.Remove(id);
            _finished.Remove(id);

            await HandleLoadedCore();
            await HandleFinishedCore();
        });
    }

    /// <summary>Answers one user's <c>sync</c>. The position and the stamp are read in the same critical section.</summary>
    public Task SyncTo(User user)
    {
        return LockedAsync(async () =>
        {
            // The stamp is read at the same instant as the position, so a client can
            // credit the request's queueing to the uplink instead of splitting it across
            // both halves the way `rtt / 2` does.
            await user.SendAsync($"sync {CurrentTimeCore()} {Stamp()}");
        });
    }

    /// <summary>
    ///     What the room is playing, for the admin panel. Under <see cref="_sync" /> like every other read:
    ///     <see cref="Items" /> is a plain <c>List&lt;T&gt;</c> and indexing it while another socket removes
    ///     a track is how a monitoring endpoint takes a room down.
    /// </summary>
    public Task<PlayerSnapshot> Snapshot()
    {
        return LockedAsync(() => new PlayerSnapshot(_playing, _loading, _currentIndex, Items.Count,
            CurrentTimeCore(), _currentIndex >= 0 && _currentIndex < Items.Count ? Items[_currentIndex] : null));
    }

    /// <summary>The room position in seconds, read under the lock.</summary>
    public Task<double> GetCurrentTime()
    {
        return LockedAsync(() => CurrentTimeCore());
    }

    /// <summary>Runs <paramref name="action" /> holding <see cref="_sync" />.</summary>
    private async Task LockedAsync(Func<Task> action)
    {
        await _sync.WaitAsync();

        try
        {
            await action();
        }
        finally
        {
            _sync.Release();
        }
    }

    /// <summary>Reads <paramref name="read" /> holding <see cref="_sync" />.</summary>
    private async Task<T> LockedAsync<T>(Func<T> read)
    {
        await _sync.WaitAsync();

        try
        {
            return read();
        }
        finally
        {
            _sync.Release();
        }
    }

    private async Task NextCore()
    {
        if (_currentIndex < Items.Count)
            _currentIndex++;

        UpdateStart();
        await SetPlayingCore(false);
        await Broadcast($"current {_currentIndex}");
    }

    /// <summary>What is playing, or <c>null</c> when the index sits past the end of the queue.</summary>
    private TrackDto? CurrentItemCore()
    {
        return _currentIndex >= 0 && _currentIndex < Items.Count ? Items[_currentIndex] : null;
    }

    /// <summary>
    ///     A queue edit that moved the current item without changing it. The <c>index</c> frame leads
    ///     the queue frame on purpose: a client re-derives what is playing from its own index every
    ///     time a list lands, so a list arriving first is read against an index that now names a
    ///     different track — a restart of a track nobody asked to change. It is not <c>current</c>,
    ///     which arms the loading barrier; nothing here stops the audio.
    /// </summary>
    private async Task ReindexCore(int index)
    {
        if (index != _currentIndex)
        {
            _currentIndex = index;
            await Broadcast($"index {_currentIndex}");
        }

        await Broadcast(QueueMessage());
    }

    private Task SetPlayingCore(bool state)
    {
        _playing = state;
        return Broadcast($"playing {_playing}");
    }

    private async Task HandleFinishedCore()
    {
        var count = messageQueue.CurrentStore.Count;
        // An empty room has nobody to wait for and nobody to tell. Without the
        // count check `0 < 0` reads as "everybody reported", so the last user
        // leaving advances the room a track on their way out.
        if (count == 0 || _finished.Count < count) return;

        _finished.Clear();
        await NextCore();
    }

    private async Task HandleLoadedCore()
    {
        var count = messageQueue.CurrentStore.Count;
        // as in HandleFinishedCore: on an empty room `0 < 0` is false, and this
        // releases the barrier — rewinding the clock and force-playing a room
        // that the next person to join then walks into mid-track. Nothing to play
        // is the same mistake in the other direction: starting the clock leaves it
        // running until something is added. That is an empty queue, and also the
        // index past the end that `next` leaves after the last track.
        if (!_loading || count == 0 || CurrentItemCore() is null || _loaded.Count < count) return;

        _loading = false;
        _loaded.Clear();
        _startTime = Stopwatch.GetTimestamp();

        await Broadcast($"seek {0d} {Stamp()}");
        await SetPlayingCore(true);
    }

    /// <summary>The room position in seconds. Both branches touch <c>StartTime</c>, so the lock must be held.</summary>
    private double CurrentTimeCore()
    {
        if (!_startTime.HasValue) return 0;

        if (_pauseTime.HasValue)
            _startTime = Stopwatch.GetTimestamp() - TimeSpanToTimestamp(_pauseTime.Value);

        return Stopwatch.GetElapsedTime(_startTime.Value).TotalSeconds;
    }

    private void UpdateStart()
    {
        _loading = true;
        _loaded.Clear();
        _finished.Clear();
        _startTime = null;
        _pauseTime = null;
    }

    /// <summary>
    ///     The queue frame, serialised straight into a pooled buffer. This used to be the single
    ///     largest allocation in the room: a JSON string, a second string for the interpolation,
    ///     and a third array for the UTF-8 encoding — three copies of the whole queue, all of them
    ///     large enough to reach gen1 on a busy room.
    /// </summary>
    private Utf8Message QueueMessage()
    {
        var message = new Utf8Message(1024);
        message.Write("queue "u8);

        using var writer = new Utf8JsonWriter(message, CustomSerializer.WriterOptions);
        JsonSerializer.Serialize(writer, Items, CustomSerializer.SerializerOptions);

        return message;
    }

    private Task Broadcast(Utf8MessageHandler handler)
    {
        return messageQueue.Add(handler.Message);
    }

    private Task Broadcast(Utf8Message message)
    {
        return messageQueue.Add(message);
    }

    /// <summary>
    ///     When this frame left the room, in Unix milliseconds. Every frame that moves the
    ///     shared clock carries one, so a client can subtract the flight this frame actually
    ///     took rather than the quickest one the link has managed lately — which is what
    ///     half a round trip amounts to, and which understates any frame that got queued.
    /// </summary>
    private static long Stamp()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    private static long TimeSpanToTimestamp(TimeSpan timeSpan)
    {
        // Multiplying first overflowed: `Ticks * Frequency` is seconds * 10^16
        // wherever the stopwatch counts nanoseconds, which it does on Linux, and
        // that passes long.MaxValue at 922 seconds. Every position past a quarter
        // of an hour came back negative — a seek into a long mix, a pause taken
        // there, and worst of all `GetCurrentTime`, which rebases `StartTime`
        // through here on every `sync` a paused room is asked for.
        // The double carries it exactly: 10^9 * 3600 is well inside 53 bits.
        return (long)(timeSpan.TotalSeconds * Stopwatch.Frequency);
    }
}

/// <summary>The room clock and queue as an operator reads them.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record PlayerSnapshot(
    bool Playing,
    bool Loading,
    int CurrentIndex,
    int QueueLength,
    double PositionSeconds,
    TrackDto? Current);
