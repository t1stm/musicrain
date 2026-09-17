using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.Voice;
using Gaida.Bot.Enums;
using Gaida.Bot.Gaida;
using Gaida.Bot.Messages;

namespace Gaida.Bot.Players;

/// <summary>
/// The old <c>Bot/Audio/Player.cs</c>: one voice connection, one queue, one statusbar. ffmpeg and
/// the transmit sink are gone — audio is Ogg/Opus from the API, passed through untouched.
/// </summary>
[SuppressMessage("Microsoft.Design", "CA1001:TypesThatOwnDisposableFieldsShouldBeDisposable",
    Justification = "Neither source holds an unmanaged handle: PlayTrackAsync disposes each linked source as " +
                    "it replaces it, and the last one plus _dying are cancelled in DisconnectAsync and then " +
                    "collected with the player. Disposing them there instead would race the prefetch and the " +
                    "feed loop, which still read their tokens as they unwind.")]
public sealed class Player
{
    private readonly CancellationTokenSource _dying = new();

    private CancellationTokenSource _trackCancellation = new();
    private TaskCompletionSource _unpaused = Completed();
    private AudioWriter? _writer;

    private bool _waitingToLeave;
    private bool _preloadedNext;

    private Task<HttpResponseMessage?>? _prefetch;
    private string? _prefetchId;

    public required DiscordClient Client { get; init; }
    public required GaidaClient Api { get; init; }
    public required ILogger Logger { get; init; }
    public required PlayerController Controller { get; init; }

    public Playlist Queue { get; } = new();
    public Statusbar Statusbar { get; } = new();

    public DiscordChannel? VoiceChannel { get; set; }
    public DiscordChannel? Channel { get; init; }
    public DiscordGuild? Guild { get; init; }
    public VoiceConnection? Connection { get; set; }

    public LoopMode LoopStatus { get; private set; } = LoopMode.None;
    public bool Paused { get; private set; }

    /// <summary>
    /// Between asking the API for a track and the first audio coming back. The body's headers do
    /// not arrive until the source is actually producing — a rate-limited YouTube fetch can sit
    /// there for a minute — so the clock has nothing to show and the statusbar animates instead.
    /// </summary>
    public bool Loading { get; private set; }
    public bool Started { get; set; }
    private bool Dead { get; set; }
    public Track? CurrentItem { get; private set; }

    public Stopwatch Stopwatch { get; } = new();
    public Stopwatch WaitingStopwatch { get; } = new();

    /// <summary>Who is in the voice channel, for the button handler's "are you here" check.</summary>
    public IReadOnlyList<DiscordMember> VoiceUsers => VoiceChannel?.Users ?? [];

    /// <summary>What the API is asked to encode at: the channel's own bitrate unless overridden.</summary>
    public int Bitrate
    {
        get
        {
            if (int.TryParse(Environment.GetEnvironmentVariable("GAIDA_OPUS_BITRATE"), out var configured))
            {
                return Math.Clamp(configured, 8, 256);
            }

            return Math.Clamp((VoiceChannel?.Bitrate ?? 112_000) / 1000, 8, 256);
        }
    }

    public async Task PlayAsync(int current = 0)
    {
        try
        {
            Statusbar.Channel = Channel;
            Statusbar.Player = this;
            _ = Task.Run(Statusbar.StartAsync);

            Queue.Current = current;

            do
            {
                if (Dead) break;

                if (Queue.Current < 0)
                {
                    Queue.Current++;
                    continue;
                }

                CurrentItem = Queue.GetCurrent();

                if (CurrentItem is not null)
                {
                    WaitingStopwatch.Reset();
                    _waitingToLeave = false;
                    Statusbar.ChangeMode(StatusbarMode.Playing);
                    await PlayTrackAsync(CurrentItem);
                }

                if (Dead) break;

                Stopwatch.Reset();

                if (LoopStatus == LoopMode.One) Queue.Current--;
                if (Queue.Current + 1 == Queue.Count && LoopStatus == LoopMode.WholeQueue)
                {
                    Queue.Current = -1;
                }

                if (Queue.EndOfQueue)
                {
                    await Task.Delay(166);
                    Statusbar.ChangeMode(StatusbarMode.Waiting);

                    if (!_waitingToLeave)
                    {
                        _waitingToLeave = true;
                        WaitingStopwatch.Restart();
                    }

                    if (WaitingStopwatch.Elapsed.TotalMinutes > 15) Dead = true;
                    continue;
                }

                Queue.Current++;
            } while (!Dead);

            await DisconnectAsync();
        }
        catch (Exception e)
        {
            Logger.Error(e, "The player loop in {Guild} died", Guild?.Name);
            await DisconnectAsync();
        }
    }

    /// <summary>
    /// Feeds one track. No silence is signalled at the end: the next track's body is already open
    /// and starts feeding immediately, so the send queue never empties and the boundary is gapless.
    /// </summary>
    private async Task PlayTrackAsync(Track item)
    {
        var previous = _trackCancellation;
        _trackCancellation = CancellationTokenSource.CreateLinkedTokenSource(_dying.Token);
        previous.Dispose();

        var token = _trackCancellation.Token;

        _preloadedNext = false;

        HttpResponseMessage? response = null;

        try
        {
            Loading = true;
            response = await TakePrefetchedAsync(item.Id) ?? await Api.OpenAudioAsync(item.Id, Bitrate, token);

            if (response is null)
            {
                Logger.Warning("Nothing playable for {ID}, skipping it", item.Id);
                return;
            }

            if (Connection is null)
            {
                // Nothing to play into, and the next track would fare no better: end the player
                // rather than let the loop burn silently through the whole queue.
                Logger.Warning("No voice connection in {Guild}, ending the player", Guild?.Name);
                Dead = true;
                return;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(token);

            Record("track", $"playing {item.DisplayName}", item.Requester?.Username);

            Connection.SetDisconnectHandler(OnConnectionLostAsync);
            _writer = Connection.CreateAudioWriter(AudioFormat.Opus);
            var scanner = new OggGranuleScanner();

            if (!Stopwatch.IsRunning) Stopwatch.Start();

            await OggPacer.FeedAsync(stream, _writer, scanner, () => Stopwatch.Elapsed,
                WaitWhilePausedAsync, _ => OnProgressAsync(item), token);
        }
        catch (OperationCanceledException)
        {
            // A skip, a goto or a disconnect. All ordinary.
        }
        catch (Exception e)
        {
            Logger.Error(e, "Playing {Name} failed", item.DisplayName);
        }
        finally
        {
            Loading = false;
            response?.Dispose();
        }
    }

    /// <summary>
    /// The connection is gone for good — the library retries on its own first and only calls this
    /// once it has given up, so there is nothing left to play into.
    /// </summary>
    private async Task OnConnectionLostAsync(VoiceDisconnectReason reason, object? state)
    {
        Logger.Warning("The voice connection in {Guild} was lost: {Reason}", Guild?.Name, reason);
        Record("lost", $"the voice connection was lost: {reason}");

        Connection = null;
        await DisconnectAsync();
    }

    /// <summary>Warms the next encode, then opens its body before this one ends.</summary>
    private async Task OnProgressAsync(Track item)
    {
        // The first chunk is the one that says the request is no longer waiting on the source.
        Loading = false;

        if (item.Length <= TimeSpan.Zero) return;

        var remaining = item.Length - Stopwatch.Elapsed;

        if (remaining > TimeSpan.FromSeconds(20)) return;

        var next = LoopStatus == LoopMode.One ? item : Queue.GetNext();
        if (next is null) return;

        if (!_preloadedNext)
        {
            _preloadedNext = true;
            _ = Api.PreloadAsync(next.Id, Bitrate);
        }

        if (remaining > TimeSpan.FromSeconds(5) || _prefetch is not null) return;

        _prefetchId = next.Id;
        _prefetch = Api.OpenAudioAsync(next.Id, Bitrate, _dying.Token);

        await Task.CompletedTask;
    }

    /// <summary>The held-open body, if it is the track we are about to play.</summary>
    private async Task<HttpResponseMessage?> TakePrefetchedAsync(string id)
    {
        var pending = _prefetch;
        var pendingId = _prefetchId;

        _prefetch = null;
        _prefetchId = null;

        if (pending is null) return null;

        if (pendingId == id) return await pending;

        // The user skipped somewhere else; throw the held response away.
        _ = pending.ContinueWith(task => task.Result?.Dispose(), TaskContinuationOptions.OnlyOnRanToCompletion);
        return null;
    }

    private Task WaitWhilePausedAsync(CancellationToken cancellationToken) =>
        _unpaused.Task.WaitAsync(cancellationToken);

    /// <summary>Records one line in the bot's audit trail, filled in from this player.</summary>
    public void Record(string kind, string detail, string? user = null) =>
        Controller.Events.Record(kind, Client.CurrentUser.Username, Guild?.Name,
            VoiceChannel?.Name, user, detail);

    public LoopMode ToggleLoop() => LoopStatus = LoopStatus switch
    {
        LoopMode.None => LoopMode.WholeQueue,
        LoopMode.WholeQueue => LoopMode.One,
        _ => LoopMode.None
    };

    /// <summary>Toggles pause. Queued audio — up to the look-ahead — still plays out.</summary>
    public void Pause()
    {
        Paused = !Paused;

        if (Paused)
        {
            _unpaused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Stopwatch.Stop();
            _writer?.SignalSilence();
            return;
        }

        Stopwatch.Start();
        _unpaused.TrySetResult();
    }

    private void Resume()
    {
        Paused = false;
        Stopwatch.Start();
        _unpaused.TrySetResult();
    }

    public void Skip(int times = 1)
    {
        Resume();

        times -= 1;
        if (Queue.Current + times < -1) return;
        if (Queue.Current + times != Queue.Count + 1) Queue.Current += times;

        CancelTrack();
    }

    public Track? GoToIndex(int index)
    {
        Resume();

        if (index >= Queue.Count && index < -1) return null;

        Queue.Current = index - 1;
        CancelTrack();
        return Queue.GetNext();
    }

    public void Shuffle() => Queue.Shuffle();

    public Track? RemoveFromQueue(int index)
    {
        try
        {
            if (index == Queue.Current)
            {
                var removed = Queue.RemoveFromQueue(index);
                Skip(0);
                return removed;
            }

            if (index >= Queue.Current) return Queue.RemoveFromQueue(index);

            var item = Queue.RemoveFromQueue(index);
            Queue.Current -= 1;
            return item;
        }
        catch (Exception e)
        {
            Logger.Warning(e, "Removing index {Index} from the queue failed", index);
            return null;
        }
    }

    public Track? RemoveFromQueue(string name)
    {
        try
        {
            var item = Queue.GetWithString(name);
            var index = Queue.Items.IndexOf(item);

            if (index == Queue.Current)
            {
                var removed = Queue.RemoveFromQueue(item);
                Skip(0);
                return removed;
            }

            if (index >= Queue.Current) return Queue.RemoveFromQueue(item);

            var removedItem = Queue.RemoveFromQueue(item);
            Queue.Current -= 1;
            return removedItem;
        }
        catch (Exception e)
        {
            Logger.Warning(e, "Removing {Name} from the queue failed", name);
            return null;
        }
    }

    /// <summary>
    /// Somebody dragged the bot to another channel. DSharpPlus.Voice does the actual moving itself —
    /// its <c>GuildMonitoringEventHandler</c> takes the same voice state update and calls
    /// <c>MoveChannelAsync</c> on the existing connection, so the audio pipeline is never torn down
    /// and playback continues across the move. All that is left for the player is to follow along,
    /// so that the voice-channel checks and the channel bitrate refer to where it now is.
    /// </summary>
    /// <remarks>
    /// Disposing the connection and building a new one here — what the old bot had to do, because
    /// VoiceNext could not move — fights that: the repository still holds the guild's registration,
    /// so the second connect throws <c>ConnectingFailedException</c>, and the library's own
    /// reconnect then works on the object that was disposed underneath it.
    /// </remarks>
    public void MovedTo(DiscordChannel channel)
    {
        Logger.Information("{Account} was moved to {Channel} in {Guild}", Client.CurrentUser.Username,
            channel.Name, Guild?.Name);
        Record("move", $"moved from {VoiceChannel?.Name ?? "—"} to {channel.Name}");

        VoiceChannel = channel;
    }

    public async Task DisconnectAsync(string message = Text.Farewell)
    {
        if (Dead && Connection is null) return;

        try
        {
            Dead = true;
            Resume();
            CancelTrack();
            await _dying.CancelAsync();

            await Statusbar.UpdateMessageAndStopAsync(message);

            _writer?.SignalCompletion();
            _writer = null;

            if (Connection is not null) await Connection.DisposeAsync();
            Connection = null;

            Controller.Remove(this);

            Logger.Information("Disconnecting from {Channel} in {Guild}", VoiceChannel?.Name,
                Guild?.Name);
            Record("leave", WaitingStopwatch.Elapsed.TotalMinutes > 15
                ? "left after fifteen minutes with an empty queue"
                : "left");
        }
        catch (Exception e)
        {
            Logger.Error(e, "Disconnecting in {Guild} failed", Guild?.Name);
        }
    }

    private void CancelTrack()
    {
        try
        {
            _trackCancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already gone; nothing to stop.
        }
    }

    private static TaskCompletionSource Completed()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }
}
