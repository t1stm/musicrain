using System.Diagnostics;
using System.Globalization;
using DSharpPlus;
using DSharpPlus.Entities;
using Gaida.Bot.Enums;
using Gaida.Bot.Players;

namespace Gaida.Bot.Messages;

/// <summary>
/// The old <c>Bot/Messages/Statusbar.cs</c>: one message per player, edited every few seconds.
/// It is sent and edited by the player's own account, not by whichever account took the command —
/// that is what spreads the edit rate limits when a guild is running several players.
/// </summary>
public sealed class Statusbar
{
    private const char EmptyBlock = '□', FullBlock = '■';

    private static readonly int DefaultUpdateDelay =
        int.TryParse(Environment.GetEnvironmentVariable("GAIDA_STATUSBAR_INTERVAL_MS"), out var configured)
            ? configured
            : 3200;

    /// <summary>Cell indices of the five dots in the unknown-length animation.</summary>
    private readonly int[] _animationCells = [0, 1, 2, 3, 4];

    public Player? Player { get; set; }
    public DiscordClient? Client { get; set; }
    public DiscordGuild? Guild { get; set; }
    public DiscordChannel? Channel { get; set; }
    public DiscordMessage? Message { get; set; }

    private bool Stopped { get; set; }
    private StatusbarMode Mode { get; set; } = StatusbarMode.Stopped;
    private int UpdateDelay { get; set; } = DefaultUpdateDelay;

    public void Stop() => Stopped = true;

    public void ChangeMode(StatusbarMode mode) => Mode = mode;

    public async Task StartAsync()
    {
        if (Message is null)
        {
            try
            {
                if (Channel is not null)
                {
                    Message = await Channel.SendMessageAsync(
                        Text.ThisMessageWillUpdateShortly().CodeBlocked());
                }
            }
            catch (Exception e)
            {
                Player?.Logger.Warning(e, "Sending the statusbar message failed");
            }
        }

        Stopped = false;
        Mode = StatusbarMode.Playing;
        var stopwatch = new Stopwatch();

        while (!Stopped)
        {
            try
            {
                stopwatch.Restart();
                await UpdateStatusbarAsync();
                UpdateDelay += (int)stopwatch.ElapsedMilliseconds / 2;
            }
            catch (Exception e)
            {
                Player?.Logger.Warning(e, "Updating the statusbar failed");

                if (e.Message.Contains("404") || e.Message.Contains("400"))
                {
                    Message = Channel is null
                        ? null
                        : await Channel.SendMessageAsync(Text.ThisMessageWillUpdateShortly().CodeBlocked());
                }
            }

            if (UpdateDelay > DefaultUpdateDelay) UpdateDelay -= DefaultUpdateDelay / 3;
            if (UpdateDelay < DefaultUpdateDelay) UpdateDelay = DefaultUpdateDelay;

            await Task.Delay(UpdateDelay);
        }
    }

    private async Task UpdateStatusbarAsync()
    {
        switch (Mode)
        {
            case StatusbarMode.Stopped:
            case StatusbarMode.Message:
                break;

            case StatusbarMode.Playing:
                await UpdatePlacementAsync();
                await UpdateMessageAsync();
                break;

            case StatusbarMode.Waiting:
                await UpdateWaitingAsync();
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(Mode), Mode, "Unknown statusbar mode.");
        }
    }

    /// <summary>Reposts the statusbar when the conversation has buried it.</summary>
    private async Task UpdatePlacementAsync()
    {
        if (Message is null || Channel is null) return;

        var after = 0;

        try
        {
            await foreach (var _ in Channel.GetMessagesAfterAsync(Message.Id, 5))
            {
                after++;
                if (after > 4) break;
            }
        }
        catch (Exception e)
        {
            Player?.Logger.Debug(e, "Reading the messages after the statusbar failed");
            return;
        }

        var clients = Player?.Controller.Clients.Count ?? 1;
        if (after <= 2 || after <= clients) return;

        await Channel.DeleteMessageAsync(Message);
        Message = null;
    }

    private async Task UpdateMessageAsync()
    {
        if (Message is null)
        {
            if (Channel is null) return;
            Message = await Channel.SendMessageAsync(GenerateBuilder());
            return;
        }

        Message = await Message.ModifyAsync(GenerateBuilder());
    }

    private async Task UpdateWaitingAsync()
    {
        if (Message is null || Player is null) return;

        var waited = Player.WaitingStopwatch.ElapsedMilliseconds;
        Message = await Message.ModifyAsync(new DiscordMessageBuilder().WithContent(
            $"```Waiting:\nFor 15 minutes and then leaving.\n" +
            $"{GenerateProgressbar(waited, 900000)} ( {Player.WaitingStopwatch.Elapsed:mm\\:ss} - 15:00 )```"));
    }

    private DiscordMessageBuilder GenerateBuilder() =>
        new DiscordMessageBuilder()
            .WithContent(GenerateStatusbar())
            .AddActionRowComponent(
                new DiscordButtonComponent(DiscordButtonStyle.Secondary, "shuffle", "Shuffle"),
                new DiscordButtonComponent(DiscordButtonStyle.Success, "back", "Previous"),
                new DiscordButtonComponent(DiscordButtonStyle.Primary, "pause", "Play / Pause"),
                new DiscordButtonComponent(DiscordButtonStyle.Success, "skip", "Next"),
                new DiscordButtonComponent(DiscordButtonStyle.Secondary, "leave", "Leave"));

    public string GenerateStatusbar()
    {
        if (Player is null) return Text.ThisMessageWillUpdateShortly().CodeBlocked();

        var player = Player;
        var next = player.Queue.GetNext();
        var requester = player.CurrentItem?.Requester;
        var length = player.CurrentItem?.Length ?? TimeSpan.Zero;
        var time = player.Stopwatch.Elapsed;

        return
            $"```{Text.Playing()}: \"{player.CurrentItem?.Kind}\"\n" +
            $"({player.Queue.Current + 1} - {player.Queue.Count}) {player.CurrentItem?.DisplayName ?? "Something's broken."}\n" +
            $"{GenerateProgressbar((long)time.TotalMilliseconds, (long)length.TotalMilliseconds)} " +
            $"( {(player.Paused ? "⏸️" : "▶️")} {Time(time)} - {(length == TimeSpan.Zero ? "∞" : Time(length))} )" +
            $"{player.LoopStatus switch { LoopMode.One => " ( 🔂 )", LoopMode.WholeQueue => " ( 🔁 )", _ => "" }}" +
            $"{(requester is null ? "" : $"\n{Text.RequestedBy()}: {requester.Username}")}" +
            $"{(next is null ? "" : $"\n\n{Text.NextUp()}: ({player.Queue.Current + 2}) {next.DisplayName}")}```";
    }

    public async Task UpdateMessageAndStopAsync(string message, bool formatted = true)
    {
        try
        {
            Stop();
            await Task.Delay(DefaultUpdateDelay);

            if (Message is null) return;

            var builder = new DiscordMessageBuilder().WithContent(formatted ? message.CodeBlocked() : message);
            builder.ClearComponents();
            await Message.ModifyAsync(builder);
        }
        catch (Exception e)
        {
            Player?.Logger.Warning(e, "Stopping the statusbar failed");
        }
    }

    /// <summary>
    /// The 32-cell bar. A total of zero means an unknown length — a live stream — and the bar runs
    /// the old five-dot animation instead of filling.
    /// </summary>
    public string GenerateProgressbar(long current, long total, int length = 32)
    {
        if (length < 4) length = 32;

        Span<char> progress = stackalloc char[length];

        if (total != 0)
        {
            var increment = total / length;
            var display = increment == 0 ? length : (int)(current / increment);
            if (display > length) display = length;
            if (display < 0) display = 0;

            for (var i = 0; i < display; i++) progress[i] = FullBlock;
            for (var i = display; i < length; i++) progress[i] = EmptyBlock;

            return progress.ToString();
        }

        for (var i = 0; i < length; i++) progress[i] = EmptyBlock;

        for (var step = 0; step < 2; step++)
        {
            for (var cell = 0; cell < _animationCells.Length; cell++)
            {
                _animationCells[cell] = _animationCells[cell] > length - 2 ? 0 : _animationCells[cell] + 1;
            }
        }

        foreach (var cell in _animationCells) progress[cell] = FullBlock;

        return progress.ToString();
    }

    public static string Time(TimeSpan timeSpan) => timeSpan.ToString("hh\\:mm\\:ss", CultureInfo.InvariantCulture);
}
