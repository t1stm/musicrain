using System.Diagnostics;
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

    private int pl0, pl1 = 1, pl2 = 2, pl3 = 3, pl4 = 4;

    public Player? Player { get; set; }
    public DiscordClient? Client { get; set; }
    public DiscordGuild? Guild { get; set; }
    public DiscordChannel? Channel { get; set; }
    public DiscordMessage? Message { get; set; }

    private bool Stopped { get; set; }
    private StatusbarMode Mode { get; set; } = StatusbarMode.Stopped;
    private int UpdateDelay { get; set; } = DefaultUpdateDelay;

    public void Stop() => this.Stopped = true;

    public void ChangeMode(StatusbarMode mode) => this.Mode = mode;

    public async Task StartAsync()
    {
        if (this.Message is null)
        {
            try
            {
                if (this.Channel is not null)
                {
                    this.Message = await this.Channel.SendMessageAsync(
                        Text.ThisMessageWillUpdateShortly().CodeBlocked());
                }
            }
            catch (Exception e)
            {
                this.Player?.Logger.Warning(e, "Sending the statusbar message failed");
            }
        }

        this.Stopped = false;
        this.Mode = StatusbarMode.Playing;
        var stopwatch = new Stopwatch();

        while (!this.Stopped)
        {
            try
            {
                stopwatch.Restart();
                await UpdateStatusbarAsync();
                this.UpdateDelay += (int)stopwatch.ElapsedMilliseconds / 2;
            }
            catch (Exception e)
            {
                this.Player?.Logger.Warning(e, "Updating the statusbar failed");

                if (e.Message.Contains("404") || e.Message.Contains("400"))
                {
                    this.Message = this.Channel is null
                        ? null
                        : await this.Channel.SendMessageAsync(Text.ThisMessageWillUpdateShortly().CodeBlocked());
                }
            }

            if (this.UpdateDelay > DefaultUpdateDelay) this.UpdateDelay -= DefaultUpdateDelay / 3;
            if (this.UpdateDelay < DefaultUpdateDelay) this.UpdateDelay = DefaultUpdateDelay;

            await Task.Delay(this.UpdateDelay);
        }
    }

    private async Task UpdateStatusbarAsync()
    {
        switch (this.Mode)
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
                throw new ArgumentOutOfRangeException(nameof(this.Mode), this.Mode, "Unknown statusbar mode.");
        }
    }

    /// <summary>Reposts the statusbar when the conversation has buried it.</summary>
    private async Task UpdatePlacementAsync()
    {
        if (this.Message is null || this.Channel is null) return;

        var after = 0;

        try
        {
            await foreach (var _ in this.Channel.GetMessagesAfterAsync(this.Message.Id, 5))
            {
                after++;
                if (after > 4) break;
            }
        }
        catch (Exception e)
        {
            this.Player?.Logger.Debug(e, "Reading the messages after the statusbar failed");
            return;
        }

        var clients = this.Player?.Controller.Clients.Count ?? 1;
        if (after <= 2 || after <= clients) return;

        await this.Channel.DeleteMessageAsync(this.Message);
        this.Message = null;
    }

    private async Task UpdateMessageAsync()
    {
        if (this.Message is null)
        {
            if (this.Channel is null) return;
            this.Message = await this.Channel.SendMessageAsync(GenerateBuilder());
            return;
        }

        this.Message = await this.Message.ModifyAsync(GenerateBuilder());
    }

    private async Task UpdateWaitingAsync()
    {
        if (this.Message is null || this.Player is null) return;

        var waited = this.Player.WaitingStopwatch.ElapsedMilliseconds;
        this.Message = await this.Message.ModifyAsync(new DiscordMessageBuilder().WithContent(
            $"```Waiting:\nFor 15 minutes and then leaving.\n" +
            $"{GenerateProgressbar(waited, 900000)} ( {this.Player.WaitingStopwatch.Elapsed:mm\\:ss} - 15:00 )```"));
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
        if (this.Player is null) return Text.ThisMessageWillUpdateShortly().CodeBlocked();

        var player = this.Player;
        var next = player.Queue.GetNext();
        var requester = player.CurrentItem?.Requester;
        var length = player.CurrentItem?.Length ?? TimeSpan.Zero;
        var time = player.Stopwatch.Elapsed;

        return
            $"```{Text.Playing()}: \"{player.CurrentItem?.Kind}\"\n" +
            $"({player.Queue.Current + 1} - {player.Queue.Count}) {player.CurrentItem?.DisplayName ?? "Something's broken."}\n" +
            $"{GenerateProgressbar((long)time.TotalMilliseconds, (long)length.TotalMilliseconds)} " +
            $"( {(player.Paused ? "⏸️" : "▶️")} {Time(time)} - {(length == TimeSpan.Zero ? "∞" : Time(length))} )" +
            $"{player.LoopStatus switch { Loop.One => " ( 🔂 )", Loop.WholeQueue => " ( 🔁 )", _ => "" }}" +
            $"{(requester is null ? "" : $"\n{Text.RequestedBy()}: {requester.Username}")}" +
            $"{(next is null ? "" : $"\n\n{Text.NextUp()}: ({player.Queue.Current + 2}) {next.DisplayName}")}```";
    }

    public async Task UpdateMessageAndStopAsync(string message, bool formatted = true)
    {
        try
        {
            Stop();
            await Task.Delay(DefaultUpdateDelay);

            if (this.Message is null) return;

            var builder = new DiscordMessageBuilder().WithContent(formatted ? message.CodeBlocked() : message);
            builder.ClearComponents();
            await this.Message.ModifyAsync(builder);
        }
        catch (Exception e)
        {
            this.Player?.Logger.Warning(e, "Stopping the statusbar failed");
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

        for (var i = 0; i < 2; i++)
        {
            this.pl0 = this.pl0 > length - 2 ? 0 : this.pl0 + 1;
            this.pl1 = this.pl1 > length - 2 ? 0 : this.pl1 + 1;
            this.pl2 = this.pl2 > length - 2 ? 0 : this.pl2 + 1;
            this.pl3 = this.pl3 > length - 2 ? 0 : this.pl3 + 1;
            this.pl4 = this.pl4 > length - 2 ? 0 : this.pl4 + 1;
        }

        progress[this.pl0] = progress[this.pl1] = progress[this.pl2] = progress[this.pl3] = progress[this.pl4] = FullBlock;

        return progress.ToString();
    }

    public static string Time(TimeSpan timeSpan) => timeSpan.ToString("hh\\:mm\\:ss");
}
