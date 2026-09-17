using System.Diagnostics;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.Voice;
using Gaida.Bot.Admin;
using Gaida.Bot.Enums;
using Gaida.Bot.Gaida;
using Gaida.Bot.Messages;
using Serilog;

namespace Gaida.Bot.Players;

/// <summary>
/// The old <c>Bot/Audio/Player.cs</c>: one voice connection, one queue, one statusbar. ffmpeg and
/// the transmit sink are gone — audio is Ogg/Opus from the API, passed through untouched.
/// </summary>
public sealed class Player
{
    private readonly CancellationTokenSource dying = new();

    private CancellationTokenSource trackCancellation = new();
    private TaskCompletionSource unpaused = Completed();
    private AudioWriter? writer;

    private bool waitingToLeave;
    private bool preloadedNext;

    private Task<HttpResponseMessage?>? prefetch;
    private string? prefetchId;

    public required DiscordClient Client { get; init; }
    public required GaidaClient Api { get; init; }
    public required ILogger Logger { get; init; }
    public required PlayerController Controller { get; init; }

    public PlayerQueue Queue { get; } = new();
    public Statusbar Statusbar { get; } = new();

    public DiscordChannel? VoiceChannel { get; set; }
    public DiscordChannel? Channel { get; set; }
    public DiscordGuild? Guild { get; set; }
    public VoiceConnection? Connection { get; set; }

    public Loop LoopStatus { get; private set; } = Loop.None;
    public bool Paused { get; private set; }
    public bool Started { get; set; }
    public bool Dead { get; private set; }
    public Track? CurrentItem { get; private set; }

    public Stopwatch Stopwatch { get; } = new();
    public Stopwatch WaitingStopwatch { get; } = new();

    /// <summary>Who is in the voice channel, for the button handler's "are you here" check.</summary>
    public IReadOnlyList<DiscordMember> VoiceUsers => this.VoiceChannel?.Users ?? [];

    /// <summary>What the API is asked to encode at: the channel's own bitrate unless overridden.</summary>
    public int Bitrate
    {
        get
        {
            if (int.TryParse(Environment.GetEnvironmentVariable("GAIDA_OPUS_BITRATE"), out var configured))
            {
                return Math.Clamp(configured, 8, 256);
            }

            return Math.Clamp((this.VoiceChannel?.Bitrate ?? 112_000) / 1000, 8, 256);
        }
    }

    public async Task PlayAsync(int current = 0)
    {
        try
        {
            this.Statusbar.Client = this.Client;
            this.Statusbar.Guild = this.Guild;
            this.Statusbar.Channel = this.Channel;
            this.Statusbar.Player = this;
            _ = Task.Run(this.Statusbar.StartAsync);

            this.Queue.Current = current;

            do
            {
                if (this.Dead) break;

                if (this.Queue.Current < 0)
                {
                    this.Queue.Current++;
                    continue;
                }

                this.CurrentItem = this.Queue.GetCurrent();

                if (this.CurrentItem is not null)
                {
                    this.WaitingStopwatch.Reset();
                    this.waitingToLeave = false;
                    this.Statusbar.ChangeMode(StatusbarMode.Playing);
                    await PlayTrackAsync(this.CurrentItem);
                }

                if (this.Dead) break;

                this.Stopwatch.Reset();

                if (this.LoopStatus == Loop.One) this.Queue.Current--;
                if (this.Queue.Current + 1 == this.Queue.Count && this.LoopStatus == Loop.WholeQueue)
                {
                    this.Queue.Current = -1;
                }

                if (this.Queue.EndOfQueue)
                {
                    await Task.Delay(166);
                    this.Statusbar.ChangeMode(StatusbarMode.Waiting);

                    if (!this.waitingToLeave)
                    {
                        this.waitingToLeave = true;
                        this.WaitingStopwatch.Restart();
                    }

                    if (this.WaitingStopwatch.Elapsed.TotalMinutes > 15) this.Dead = true;
                    continue;
                }

                this.Queue.Current++;
            } while (!this.Dead);

            await DisconnectAsync();
        }
        catch (Exception e)
        {
            this.Logger.Error(e, "The player loop in {Guild} died", this.Guild?.Name);
            await DisconnectAsync();
        }
    }

    /// <summary>
    /// Feeds one track. No silence is signalled at the end: the next track's body is already open
    /// and starts feeding immediately, so the send queue never empties and the boundary is gapless.
    /// </summary>
    private async Task PlayTrackAsync(Track item)
    {
        var previous = this.trackCancellation;
        this.trackCancellation = CancellationTokenSource.CreateLinkedTokenSource(this.dying.Token);
        previous.Dispose();

        var token = this.trackCancellation.Token;

        this.preloadedNext = false;

        HttpResponseMessage? response = null;

        try
        {
            response = await TakePrefetchedAsync(item.Id) ?? await this.Api.OpenAudioAsync(item.Id, this.Bitrate, token);

            if (response is null)
            {
                this.Logger.Warning("Nothing playable for {Id}, skipping it", item.Id);
                return;
            }

            if (this.Connection is null)
            {
                // Nothing to play into, and the next track would fare no better: end the player
                // rather than let the loop burn silently through the whole queue.
                this.Logger.Warning("No voice connection in {Guild}, ending the player", this.Guild?.Name);
                this.Dead = true;
                return;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(token);

            Record("track", $"playing {item.DisplayName}", item.Requester?.Username);

            this.Connection.SetDisconnectHandler(OnConnectionLostAsync);
            this.writer = this.Connection.CreateAudioWriter(AudioFormat.Opus);
            var scanner = new OggGranuleScanner();

            if (!this.Stopwatch.IsRunning) this.Stopwatch.Start();

            await OggPacer.FeedAsync(stream, this.writer, scanner, () => this.Stopwatch.Elapsed,
                WaitWhilePausedAsync, fed => OnProgressAsync(item, fed), token);
        }
        catch (OperationCanceledException)
        {
            // A skip, a goto or a disconnect. All ordinary.
        }
        catch (Exception e)
        {
            this.Logger.Error(e, "Playing {Name} failed", item.DisplayName);
        }
        finally
        {
            response?.Dispose();
        }
    }

    /// <summary>
    /// The connection is gone for good — the library retries on its own first and only calls this
    /// once it has given up, so there is nothing left to play into.
    /// </summary>
    private async Task OnConnectionLostAsync(VoiceDisconnectReason reason, object? state)
    {
        this.Logger.Warning("The voice connection in {Guild} was lost: {Reason}", this.Guild?.Name, reason);
        Record("lost", $"the voice connection was lost: {reason}");

        this.Connection = null;
        await DisconnectAsync();
    }

    /// <summary>Warms the next encode, then opens its body before this one ends.</summary>
    private async Task OnProgressAsync(Track item, TimeSpan fed)
    {
        if (item.Length <= TimeSpan.Zero) return;

        var remaining = item.Length - this.Stopwatch.Elapsed;

        if (remaining > TimeSpan.FromSeconds(20)) return;

        var next = this.LoopStatus == Loop.One ? item : this.Queue.GetNext();
        if (next is null) return;

        if (!this.preloadedNext)
        {
            this.preloadedNext = true;
            _ = this.Api.PreloadAsync(next.Id, this.Bitrate);
        }

        if (remaining > TimeSpan.FromSeconds(5) || this.prefetch is not null) return;

        this.prefetchId = next.Id;
        this.prefetch = this.Api.OpenAudioAsync(next.Id, this.Bitrate, this.dying.Token);

        await Task.CompletedTask;
    }

    /// <summary>The held-open body, if it is the track we are about to play.</summary>
    private async Task<HttpResponseMessage?> TakePrefetchedAsync(string id)
    {
        var pending = this.prefetch;
        var pendingId = this.prefetchId;

        this.prefetch = null;
        this.prefetchId = null;

        if (pending is null) return null;

        if (pendingId == id) return await pending;

        // The user skipped somewhere else; throw the held response away.
        _ = pending.ContinueWith(task => task.Result?.Dispose(), TaskContinuationOptions.OnlyOnRanToCompletion);
        return null;
    }

    private Task WaitWhilePausedAsync(CancellationToken ct) => this.unpaused.Task.WaitAsync(ct);

    /// <summary>Records one line in the bot's audit trail, filled in from this player.</summary>
    public void Record(string kind, string detail, string? user = null) =>
        this.Controller.Events.Record(kind, this.Client.CurrentUser.Username, this.Guild?.Name,
            this.VoiceChannel?.Name, user, detail);

    public Loop ToggleLoop() => this.LoopStatus = this.LoopStatus switch
    {
        Loop.None => Loop.WholeQueue,
        Loop.WholeQueue => Loop.One,
        Loop.One => Loop.None,
        _ => Loop.None
    };

    /// <summary>Toggles pause. Queued audio — up to the look-ahead — still plays out.</summary>
    public void Pause()
    {
        this.Paused = !this.Paused;

        if (this.Paused)
        {
            this.unpaused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            this.Stopwatch.Stop();
            this.writer?.SignalSilence();
            return;
        }

        this.Stopwatch.Start();
        this.unpaused.TrySetResult();
    }

    private void Resume()
    {
        this.Paused = false;
        this.Stopwatch.Start();
        this.unpaused.TrySetResult();
    }

    public void Skip(int times = 1)
    {
        Resume();

        times -= 1;
        if (this.Queue.Current + times < -1) return;
        if (this.Queue.Current + times != this.Queue.Count + 1) this.Queue.Current += times;

        CancelTrack();
    }

    public Track? GoToIndex(int index)
    {
        Resume();

        if (index >= this.Queue.Count && index < -1) return null;

        this.Queue.Current = index - 1;
        CancelTrack();
        return this.Queue.GetNext();
    }

    public void Shuffle() => this.Queue.Shuffle();

    public Track? RemoveFromQueue(int index)
    {
        try
        {
            if (index == this.Queue.Current)
            {
                var removed = this.Queue.RemoveFromQueue(index);
                Skip(0);
                return removed;
            }

            if (index >= this.Queue.Current) return this.Queue.RemoveFromQueue(index);

            var item = this.Queue.RemoveFromQueue(index);
            this.Queue.Current -= 1;
            return item;
        }
        catch (Exception e)
        {
            this.Logger.Warning(e, "Removing index {Index} from the queue failed", index);
            return null;
        }
    }

    public Track? RemoveFromQueue(string name)
    {
        try
        {
            var item = this.Queue.GetWithString(name);
            var index = this.Queue.Items.IndexOf(item);

            if (index == this.Queue.Current)
            {
                var removed = this.Queue.RemoveFromQueue(item);
                Skip(0);
                return removed;
            }

            if (index >= this.Queue.Current) return this.Queue.RemoveFromQueue(item);

            var removedItem = this.Queue.RemoveFromQueue(item);
            this.Queue.Current -= 1;
            return removedItem;
        }
        catch (Exception e)
        {
            this.Logger.Warning(e, "Removing {Name} from the queue failed", name);
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
        this.Logger.Information("{Account} was moved to {Channel} in {Guild}", this.Client.CurrentUser.Username,
            channel.Name, this.Guild?.Name);
        Record("move", $"moved from {this.VoiceChannel?.Name ?? "—"} to {channel.Name}");

        this.VoiceChannel = channel;
    }

    public async Task DisconnectAsync(string message = Text.Farewell)
    {
        if (this.Dead && this.Connection is null) return;

        try
        {
            this.Dead = true;
            Resume();
            CancelTrack();
            await this.dying.CancelAsync();

            await this.Statusbar.UpdateMessageAndStopAsync(message);

            this.writer?.SignalCompletion();
            this.writer = null;

            if (this.Connection is not null) await this.Connection.DisposeAsync();
            this.Connection = null;

            this.Controller.Remove(this);

            this.Logger.Information("Disconnecting from {Channel} in {Guild}", this.VoiceChannel?.Name,
                this.Guild?.Name);
            Record("leave", this.WaitingStopwatch.Elapsed.TotalMinutes > 15
                ? "left after fifteen minutes with an empty queue"
                : "left");
        }
        catch (Exception e)
        {
            this.Logger.Error(e, "Disconnecting in {Guild} failed", this.Guild?.Name);
        }
    }

    private void CancelTrack()
    {
        try
        {
            this.trackCancellation.Cancel();
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
