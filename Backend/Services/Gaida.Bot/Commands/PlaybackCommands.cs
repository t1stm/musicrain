using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using DSharpPlus.Commands;
using DSharpPlus.Commands.ArgumentModifiers;
using DSharpPlus.Commands.Processors.SlashCommands;
using DSharpPlus.Commands.Trees.Metadata;
using DSharpPlus.Entities;
using DSharpPlus.Exceptions;
using DSharpPlus.Interactivity.Extensions;
using DSharpPlus.Voice;
using Gaida.Bot.Gaida;
using Gaida.Bot.Players;
using Gaida.Bot.Tools;
using JetBrains.Annotations;

namespace Gaida.Bot.Commands;

/// <summary>
/// The playback and queue commands, with the old bot's names, aliases and arguments. Registered on
/// the master account only — a second account listening for the same prefix would answer twice.
/// </summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class PlaybackCommands(PlayerController controller, GaidaClient api, ILogger logger)
{
    /// <summary>Discord's longest message, its most options in a select, and its longest option text.</summary>
    private const int MessageLimit = 2000, SelectLimit = 25, OptionTextLimit = 100;

    private const string SelectId = "dropdown";

    /// <summary>What <c>"Lyrics for {query}: \n"</c> adds around the lyrics, give or take.</summary>
    private const int LyricsPreambleLength = 19;

    [Command("play")]
    [TextAlias("p", "плаъ", "п", "udri", "удри")]
    public ValueTask PlayAsync(CommandContext ctx, [RemainingText] string search = "") =>
        PlayCoreAsync(ctx, search, false);

    [Command("playselect")]
    [TextAlias("ps")]
    public ValueTask PlaySelectAsync(CommandContext ctx, [RemainingText] string search = "") =>
        PlayCoreAsync(ctx, search, true);

    [Command("playnext")]
    [TextAlias("pn", "плаън", "пн")]
    public async ValueTask PlayNextAsync(CommandContext ctx, [RemainingText] string search = "")
    {
        await ctx.DeferResponseAsync();

        var voice = await UserVoiceChannelAsync(ctx, "playnext");
        if (voice is null) return;

        var player = controller.GetPlayer(voice);
        if (player?.Connection is null)
        {
            await PlayCoreAsync(ctx, search, false);
            return;
        }

        if (await MovedToFrontAsync(ctx, player, search)) return;

        var items = new List<Track>();
        await foreach (var track in api.TracksForAsync(search))
        {
            track.Requester = ctx.Member;
            items.Add(track);
        }

        if (items.Count == 0)
        {
            await Replies.AcknowledgeAsync(ctx, player, Text.NoResultsFound(search));
            return;
        }

        player.Queue.AddToQueueNext(items);
        await Replies.AcknowledgeAsync(ctx, player, items.Count > 1
            ? Text.PlayingItemAfterThis(search)
            : Text.PlayingItemAfterThis(player.Queue.IndexOf(items[0]) + 1, items[0].DisplayName));
    }

    /// <summary>
    /// A bare number moves that queue entry to the front instead of searching for it. One past the end
    /// of the queue is said so, and then searched for like any other text.
    /// </summary>
    private static async ValueTask<bool> MovedToFrontAsync(CommandContext ctx, Player player, string search)
    {
        if (!int.TryParse(search, out var index) || index < 1) return false;

        if (index > player.Queue.Count)
        {
            await Replies.AcknowledgeAsync(ctx, player, Text.NumberBiggerThanQueueLength(index));
            return false;
        }

        var moved = player.Queue.Items[index - 1];
        player.Queue.RemoveFromQueue(moved);
        player.Queue.AddToQueueNext(moved);
        await Replies.AcknowledgeAsync(ctx, player,
            Text.PlayingItemAfterThis(player.Queue.IndexOf(moved) + 1, moved.DisplayName));
        return true;
    }

    [Command("skip")]
    [TextAlias("next", "скип", "неьт")]
    public async ValueTask SkipAsync(CommandContext ctx, int times = 1)
    {
        var player = await PlayerForAsync(ctx, "skip");
        if (player is null) return;

        player.Skip(times);
        await Replies.AcknowledgeAsync(ctx, player, Text.SkippingOneTime);
    }

    [Command("previous")]
    [TextAlias("back", "prev", "бацк", "прев", "прежиоус")]
    public async ValueTask PreviousAsync(CommandContext ctx, int times = 1)
    {
        var player = await PlayerForAsync(ctx, "previous");
        if (player is null) return;

        player.Skip(-times);
        await Replies.AcknowledgeAsync(ctx, player, Text.SkippingOneTimeBack);
    }

    [Command("pause")]
    [TextAlias("паусе")]
    public async ValueTask PauseAsync(CommandContext ctx)
    {
        var player = await PlayerForAsync(ctx, "pause");
        if (player is null) return;

        player.Pause();
        await Replies.AcknowledgeAsync(ctx, player, player.Paused ? Text.PausingThePlayer : Text.UnpausingThePlayer);
    }

    [Command("leave")]
    [TextAlias("l", "stop", "леаже", "л", "стоп", "с", "s", "die", "дие")]
    public async ValueTask LeaveAsync(CommandContext ctx)
    {
        var player = await PlayerForAsync(ctx, "leave");
        if (player is null) return;

        if (ctx is SlashCommandContext) await ctx.RespondAsync(Text.Farewell.CodeBlocked());
        await player.DisconnectAsync();
    }

    [Command("loop")]
    [TextAlias("лооп")]
    public async ValueTask LoopAsync(CommandContext ctx)
    {
        var player = await PlayerForAsync(ctx, "loop");
        if (player is null) return;

        await Replies.AcknowledgeAsync(ctx, player, Text.LoopStatusUpdate(player.ToggleLoop()));
    }

    [Command("shuffle")]
    [TextAlias("rand", "схуффле", "ранд")]
    public async ValueTask ShuffleAsync(CommandContext ctx, [RemainingText] string seed = "")
    {
        var player = await PlayerForAsync(ctx, "shuffle");
        if (player is null) return;

        if (int.TryParse(seed, out var parsed))
        {
            logger.Debug("Shuffling {Guild} with seed {Seed}", player.Guild?.Name, parsed);
            player.Queue.ShuffleWithSeed(parsed);
        }
        else
        {
            player.Shuffle();
        }

        await Replies.AcknowledgeAsync(ctx, player, Text.ShufflingTheQueue);
    }

    [Command("remove")]
    [TextAlias("r", "rm", "реможе", "рм", "р")]
    public async ValueTask RemoveAsync(CommandContext ctx, [RemainingText] string remove = "")
    {
        var player = await PlayerForAsync(ctx, "remove");
        if (player is null) return;

        var item = int.TryParse(remove, out var index)
            ? player.RemoveFromQueue(index - 1)
            : player.RemoveFromQueue(remove);

        await Replies.AcknowledgeAsync(ctx, player,
            item is null ? Text.FailedToRemove(remove) : Text.RemovingItem(item.DisplayName));
    }

    [Command("move")]
    [TextAlias("m", "mv", "м", "може", "мж")]
    public async ValueTask MoveAsync(CommandContext ctx, [RemainingText] string move = "")
    {
        var player = await PlayerForAsync(ctx, "move");
        if (player is null) return;

        var parts = move.Split(' ');
        if (parts.Length > 1 && int.TryParse(parts[0], out var from) && int.TryParse(parts[1], out var to))
        {
            await Replies.AcknowledgeAsync(ctx, player, player.Queue.Move(from - 1, to - 1, out var moved)
                ? Text.Moved(from, moved.DisplayName, to)
                : Text.FailedToMove);
            return;
        }

        if (!move.Contains("!to"))
        {
            await Replies.AcknowledgeAsync(ctx, player, Text.InvalidMoveFormat);
            return;
        }

        var tracks = move.Split("!to");
        await Replies.AcknowledgeAsync(ctx, player, player.Queue.Move(tracks[0], tracks[1], out var one, out var two)
            ? Text.SwitchedThePlacesOf(one.DisplayName, two.DisplayName)
            : Text.FailedToMove);
    }

    [Command("goto")]
    [TextAlias("гото", "go", "го", "skipto", "скипто")]
    public async ValueTask GoToAsync(CommandContext ctx, int index)
    {
        var player = await PlayerForAsync(ctx, "goto");
        if (player is null) return;

        var item = player.GoToIndex(index - 1);
        await Replies.AcknowledgeAsync(ctx, player, Text.GoingTo(index, item?.DisplayName));
    }

    [Command("list")]
    [TextAlias("queue", "лист", "яуеуе")]
    public async ValueTask ListAsync(CommandContext ctx)
    {
        var player = await PlayerForAsync(ctx, "list");
        if (player is null) return;

        var builder = new DiscordMessageBuilder()
            .WithContent(Text.CurrentQueue.CodeBlocked())
            .AddFile("queue.txt", new MemoryStream(Encoding.UTF8.GetBytes(player.Queue.ToString())));

        if (ctx is SlashCommandContext || player.Channel is null) await ctx.RespondAsync(builder);
        else await player.Channel.SendMessageAsync(builder);
    }

    [Command("clear")]
    public async ValueTask ClearAsync(CommandContext ctx)
    {
        var player = await PlayerForAsync(ctx, "clear");
        if (player is null) return;

        player.Queue.Clear();
        await Replies.AcknowledgeAsync(ctx, player, Text.CurrentQueue);
    }

    [Command("lyrics")]
    public async ValueTask LyricsAsync(CommandContext ctx, [RemainingText] string search = "")
    {
        await ctx.DeferResponseAsync();

        if (await LyricsTrackAsync(ctx, search) is not { } track) return;

        var query = track.DisplayName;
        var lyrics = await api.LyricsAsync(track.Id);
        if (lyrics?.Text is null || string.IsNullOrWhiteSpace(lyrics.Text))
        {
            await Replies.RefuseAsync(ctx, Text.NoResultsFoundLyrics(query));
            return;
        }

        if (lyrics.Text.Length + query.Length + LyricsPreambleLength > MessageLimit)
        {
            await ctx.RespondAsync(new DiscordMessageBuilder()
                .WithContent(Text.LyricsLong.CodeBlocked())
                .AddFile("lyrics.txt", new MemoryStream(Encoding.UTF8.GetBytes(lyrics.Text))));
            return;
        }

        await ctx.RespondAsync($"Lyrics for {query}: \n{lyrics.Text}");
    }

    /// <summary>What is playing in the caller's channel, or the first hit for what they typed. Refuses when neither.</summary>
    private async Task<Track?> LyricsTrackAsync(CommandContext ctx, string search)
    {
        if (!string.IsNullOrWhiteSpace(search))
        {
            await foreach (var track in api.SearchAsync(search)) return track;

            await Replies.RefuseAsync(ctx, Text.NoResultsFoundLyrics(search));
            return null;
        }

        var channelId = ctx.Member?.VoiceState?.ChannelId;
        if (channelId is null || ctx.Guild is null || !ctx.Guild.Channels.TryGetValue(channelId.Value, out var voice))
        {
            await Replies.RefuseAsync(ctx, Text.UserNotInChannelLyrics);
            return null;
        }

        if (controller.GetPlayer(voice)?.CurrentItem is { } current) return current;

        await Replies.RefuseAsync(ctx, Text.BotNotInChannelLyrics);
        return null;
    }

    private async ValueTask PlayCoreAsync(CommandContext ctx, string search, bool select)
    {
        await ctx.DeferResponseAsync();

        var voice = await UserVoiceChannelAsync(ctx, "play");
        if (voice is null) return;

        var player = controller.GetPlayer(voice, ctx.Channel, true);
        if (player is null)
        {
            await Replies.RefuseAsync(ctx, Text.NoFreeBotAccounts);
            return;
        }

        var startedBefore = player.Started;

        try
        {
            IAsyncEnumerable<Track> source;

            if (select && !search.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                var chosen = await SelectAsync(ctx, player, search);
                if (chosen is null)
                {
                    if (!startedBefore) controller.Remove(player);
                    return;
                }

                source = One(chosen);
            }
            else
            {
                source = api.TracksForAsync(search);
            }

            var added = await QueueAsync(ctx, player, source);

            if (added.Count == 0)
            {
                if (!startedBefore) controller.Remove(player);
                await Replies.RefuseAsync(ctx, Text.NoResultsFound(search));
                return;
            }

            await ReportAddedAsync(ctx, player, search, added, startedBefore);
        }
        catch (Exception e)
        {
            logger.Error(e, "Playing {Search} failed", search);
            if (!player.Started) controller.Remove(player);
        }
    }

    /// <summary>Queues every track as it arrives, starting the player on the first so playback need not wait for the rest.</summary>
    private async Task<List<Track>> QueueAsync(CommandContext ctx, Player player, IAsyncEnumerable<Track> source)
    {
        var added = new List<Track>();

        await foreach (var track in source)
        {
            track.Requester = ctx.Member;
            player.Queue.AddToQueue(track);
            added.Add(track);

            if (!player.Started) await StartAsync(ctx, player);
        }

        return added;
    }

    /// <summary>A player that was already going says what joined its queue; a new one's statusbar says it for it.</summary>
    private static async ValueTask ReportAddedAsync(CommandContext ctx, Player player, string search,
        List<Track> added, bool startedBefore)
    {
        if (startedBefore)
        {
            await Replies.AcknowledgeAsync(ctx, player, added.Count > 1
                ? Text.AddedItem(search)
                : Text.AddedItem($"({player.Queue.IndexOf(added[0]) + 1}) - {added[0].DisplayName}"));
        }
        else if (ctx is SlashCommandContext)
        {
            await ctx.RespondAsync(Text.AddedItem(added[0].DisplayName).CodeBlocked());
        }
    }

    /// <summary>The old select dropdown: 25 results, one minute, the chosen track's ID back.</summary>
    private async Task<Track?> SelectAsync(CommandContext ctx, Player player, string search)
    {
        var results = new List<Track>();
        await foreach (var track in api.SearchAsync(search))
        {
            results.Add(track);
            if (results.Count == SelectLimit) break;
        }

        if (results.Count == 0)
        {
            await Replies.RefuseAsync(ctx, Text.NoResultsFound(search));
            return null;
        }

        var options = results.Select((track, idx) => new DiscordSelectComponentOption(
            Truncate(track.Name, OptionTextLimit), idx.ToString(CultureInfo.InvariantCulture),
            Truncate(track.Artist, OptionTextLimit))).ToList();

        var channel = player.Channel ?? ctx.Channel;
        var message = await channel.SendMessageAsync(new DiscordMessageBuilder()
            .WithContent(Text.SelectVideo.CodeBlocked())
            .AddActionRowComponent(new DiscordSelectComponent(SelectId, null!, options)));

        var response = await message.WaitForSelectAsync(ctx.User, SelectId, TimeSpan.FromSeconds(60));

        if (response.TimedOut)
        {
            await message.ModifyAsync(new DiscordMessageBuilder()
                .WithContent(Text.SelectVideoTimeout.CodeBlocked()));
            return null;
        }

        await message.ModifyAsync(new DiscordMessageBuilder()
            .WithContent(Text.ThisMessageWillUpdateShortly.CodeBlocked()));

        // The statusbar takes over the message the user just answered, as the old bot did.
        player.Statusbar.Message = message;

        var values = response.Result.Values;
        return values.Length > 0 && int.TryParse(values[0], out var index) && index < results.Count
            ? results[index]
            : null;
    }

    private static async IAsyncEnumerable<Track> One(Track track)
    {
        yield return track;
        await Task.CompletedTask;
    }

    private async Task StartAsync(CommandContext ctx, Player player)
    {
        player.Started = true;

        try
        {
            var channel = player.VoiceChannel ?? throw new InvalidOperationException("The player has no voice channel.");
            player.Connection = await channel.ConnectAsync();
            _ = Task.Run(() => player.PlayAsync());
        }
        catch (Exception e)
        {
            logger.Error(e, "Connecting to {Channel} failed", player.VoiceChannel?.Name);
            player.Started = false;
            controller.Remove(player);
            await Replies.RefuseAsync(ctx, Text.DiscordDidTheFunny);
        }
    }

    /// <summary>The two guards every command starts with: user in a channel, bot in that channel.</summary>
    private async ValueTask<Player?> PlayerForAsync(CommandContext ctx, string command)
    {
        var voice = await UserVoiceChannelAsync(ctx, command);
        if (voice is null) return null;

        var player = controller.GetPlayer(voice);
        if (player is not null) return player;

        await Replies.RefuseAsync(ctx, Text.BotIsNotInTheChannel);
        return null;
    }

    private static async ValueTask<DiscordChannel?> UserVoiceChannelAsync(CommandContext ctx, string command)
    {
        var channelId = ctx.Member?.VoiceState?.ChannelId;

        if (channelId is not null && ctx.Guild is not null && ctx.Guild.Channels.TryGetValue(channelId.Value, out var channel))
        {
            return channel;
        }

        await Replies.RefuseAsync(ctx, Text.EnterChannelBeforeCommand(command));
        return null;
    }

    private static string Truncate(string value, int length) =>
        string.IsNullOrEmpty(value) ? "—" : value.Length <= length ? value : value[..length];
}
