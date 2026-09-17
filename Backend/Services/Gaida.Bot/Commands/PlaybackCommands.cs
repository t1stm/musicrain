using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using DSharpPlus.Commands;
using DSharpPlus.Commands.ArgumentModifiers;
using DSharpPlus.Commands.Processors.SlashCommands;
using DSharpPlus.Commands.Trees.Metadata;
using DSharpPlus.Entities;
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

        // A bare number moves that queue entry to the front instead of searching for it.
        if (int.TryParse(search, out var index))
        {
            if (index > player.Queue.Count)
            {
                await AcknowledgeAsync(ctx, player, Text.NumberBiggerThanQueueLength(index));
            }
            else
            {
                var moved = player.Queue.Items[index - 1];
                player.Queue.RemoveFromQueue(moved);
                player.Queue.AddToQueueNext(moved);
                await AcknowledgeAsync(ctx, player,
                    Text.PlayingItemAfterThis(player.Queue.Items.IndexOf(moved) + 1, moved.DisplayName));
                return;
            }
        }

        var items = new List<Track>();
        await foreach (var track in TracksForAsync(search))
        {
            track.Requester = ctx.Member;
            items.Add(track);
        }

        if (items.Count == 0)
        {
            await AcknowledgeAsync(ctx, player, Text.NoResultsFound(search));
            return;
        }

        player.Queue.AddToQueueNext(items);
        await AcknowledgeAsync(ctx, player, items.Count > 1
            ? Text.PlayingItemAfterThis(search)
            : Text.PlayingItemAfterThis(player.Queue.Items.IndexOf(items[0]) + 1, items[0].DisplayName));
    }

    [Command("skip")]
    [TextAlias("next", "скип", "неьт")]
    public async ValueTask SkipAsync(CommandContext ctx, int times = 1)
    {
        var player = await PlayerForAsync(ctx, "skip");
        if (player is null) return;

        player.Skip(times);
        await AcknowledgeAsync(ctx, player, Text.SkippingOneTime());
    }

    [Command("previous")]
    [TextAlias("back", "prev", "бацк", "прев", "прежиоус")]
    public async ValueTask PreviousAsync(CommandContext ctx, int times = 1)
    {
        var player = await PlayerForAsync(ctx, "previous");
        if (player is null) return;

        player.Skip(-times);
        await AcknowledgeAsync(ctx, player, Text.SkippingOneTimeBack());
    }

    [Command("pause")]
    [TextAlias("паусе")]
    public async ValueTask PauseAsync(CommandContext ctx)
    {
        var player = await PlayerForAsync(ctx, "pause");
        if (player is null) return;

        player.Pause();
        await AcknowledgeAsync(ctx, player, player.Paused ? Text.PausingThePlayer() : Text.UnpausingThePlayer());
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

        await AcknowledgeAsync(ctx, player, Text.LoopStatusUpdate(player.ToggleLoop()));
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

        await AcknowledgeAsync(ctx, player, Text.ShufflingTheQueue());
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

        await AcknowledgeAsync(ctx, player,
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
            await AcknowledgeAsync(ctx, player, player.Queue.Move(from - 1, to - 1, out var moved)
                ? Text.Moved(from, moved.DisplayName, to)
                : Text.FailedToMove());
            return;
        }

        if (!move.Contains("!to"))
        {
            await AcknowledgeAsync(ctx, player, Text.InvalidMoveFormat());
            return;
        }

        var tracks = move.Split("!to");
        await AcknowledgeAsync(ctx, player, player.Queue.Move(tracks[0], tracks[1], out var one, out var two)
            ? Text.SwitchedThePlacesOf(one.DisplayName, two.DisplayName)
            : Text.FailedToMove());
    }

    [Command("goto")]
    [TextAlias("гото", "go", "го", "skipto", "скипто")]
    public async ValueTask GoToAsync(CommandContext ctx, int index)
    {
        var player = await PlayerForAsync(ctx, "goto");
        if (player is null) return;

        var item = player.GoToIndex(index - 1);
        await AcknowledgeAsync(ctx, player, Text.GoingTo(index, item?.DisplayName));
    }

    [Command("list")]
    [TextAlias("queue", "лист", "яуеуе")]
    public async ValueTask ListAsync(CommandContext ctx)
    {
        var player = await PlayerForAsync(ctx, "list");
        if (player is null) return;

        var builder = new DiscordMessageBuilder()
            .WithContent(Text.CurrentQueue().CodeBlocked())
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
        await AcknowledgeAsync(ctx, player, Text.CurrentQueue());
    }

    [Command("lyrics")]
    public async ValueTask LyricsAsync(CommandContext ctx, [RemainingText] string search = "")
    {
        await ctx.DeferResponseAsync();

        string id;
        string query;

        if (string.IsNullOrWhiteSpace(search))
        {
            var channelId = ctx.Member?.VoiceState?.ChannelId;
            if (channelId is null || ctx.Guild is null || !ctx.Guild.Channels.TryGetValue(channelId.Value, out var voice))
            {
                await RefuseAsync(ctx, Text.UserNotInChannelLyrics());
                return;
            }

            var player = controller.GetPlayer(voice);
            if (player?.CurrentItem is null)
            {
                await RefuseAsync(ctx, Text.BotNotInChannelLyrics());
                return;
            }

            id = player.CurrentItem.Id;
            query = player.CurrentItem.DisplayName;
        }
        else
        {
            Track? first = null;
            await foreach (var track in api.SearchAsync(search))
            {
                first = track;
                break;
            }

            if (first is null)
            {
                await RefuseAsync(ctx, Text.NoResultsFoundLyrics(search));
                return;
            }

            id = first.Id;
            query = first.DisplayName;
        }

        var lyrics = await api.LyricsAsync(id);
        if (lyrics?.Text is null || string.IsNullOrWhiteSpace(lyrics.Text))
        {
            await RefuseAsync(ctx, Text.NoResultsFoundLyrics(query));
            return;
        }

        if (lyrics.Text.Length + query.Length + 19 > 2000)
        {
            await ctx.RespondAsync(new DiscordMessageBuilder()
                .WithContent(Text.LyricsLong().CodeBlocked())
                .AddFile("lyrics.txt", new MemoryStream(Encoding.UTF8.GetBytes(lyrics.Text))));
            return;
        }

        await ctx.RespondAsync($"Lyrics for {query}: \n{lyrics.Text}");
    }

    private async ValueTask PlayCoreAsync(CommandContext ctx, string search, bool select)
    {
        await ctx.DeferResponseAsync();

        var voice = await UserVoiceChannelAsync(ctx, "play");
        if (voice is null) return;

        var player = controller.GetPlayer(voice, ctx.Channel, true);
        if (player is null)
        {
            await RefuseAsync(ctx, Text.NoFreeBotAccounts());
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
                source = TracksForAsync(search);
            }

            var added = new List<Track>();

            await foreach (var track in source)
            {
                track.Requester = ctx.Member;
                player.Queue.AddToQueue(track);
                added.Add(track);

                if (!player.Started) await StartAsync(ctx, player);
            }

            if (added.Count == 0)
            {
                if (!startedBefore) controller.Remove(player);
                await RefuseAsync(ctx, Text.NoResultsFound(search));
                return;
            }

            if (startedBefore)
            {
                await AcknowledgeAsync(ctx, player, added.Count > 1
                    ? Text.AddedItem(search)
                    : Text.AddedItem($"({player.Queue.Items.IndexOf(added[0]) + 1}) - {added[0].DisplayName}"));
            }
            else if (ctx is SlashCommandContext)
            {
                await ctx.RespondAsync(Text.AddedItem(added[0].DisplayName).CodeBlocked());
            }
        }
        catch (Exception e)
        {
            logger.Error(e, "Playing {Search} failed", search);
            if (!player.Started) controller.Remove(player);
        }
    }

    /// <summary>The old select dropdown: 25 results, one minute, the chosen track's ID back.</summary>
    private async Task<Track?> SelectAsync(CommandContext ctx, Player player, string search)
    {
        var results = new List<Track>();
        await foreach (var track in api.SearchAsync(search))
        {
            results.Add(track);
            if (results.Count == 25) break;
        }

        if (results.Count == 0)
        {
            await RefuseAsync(ctx, Text.NoResultsFound(search));
            return null;
        }

        var options = results.Select((track, index) => new DiscordSelectComponentOption(
            Truncate(track.Name, 100), index.ToString(CultureInfo.InvariantCulture),
            Truncate(track.Artist, 100))).ToList();

        var channel = player.Channel ?? ctx.Channel;
        var message = await channel.SendMessageAsync(new DiscordMessageBuilder()
            .WithContent(Text.SelectVideo().CodeBlocked())
            .AddActionRowComponent(new DiscordSelectComponent("dropdown", null!, options)));

        var response = await message.WaitForSelectAsync(ctx.User, "dropdown", TimeSpan.FromSeconds(60));

        if (response.TimedOut)
        {
            await message.ModifyAsync(new DiscordMessageBuilder()
                .WithContent(Text.SelectVideoTimeout().CodeBlocked()));
            return null;
        }

        await message.ModifyAsync(new DiscordMessageBuilder()
            .WithContent(Text.ThisMessageWillUpdateShortly().CodeBlocked()));

        // The statusbar takes over the message the user just answered, as the old bot did.
        player.Statusbar.Message = message;

        var values = response.Result.Values;
        return values.Length > 0 && int.TryParse(values[0], out var index) && index < results.Count
            ? results[index]
            : null;
    }

    /// <summary>
    /// What a query puts in the queue: every track of a playlist, the one track a link names, or
    /// the single best hit for a search term. Only a playlist queues more than one thing.
    /// </summary>
    private async IAsyncEnumerable<Track> TracksForAsync(string search,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var resolution = await api.ResolveAsync(search, cancellationToken);

        // A link the resolver already turned into a track: no search needed at all.
        if (resolution?.Result is not null)
        {
            yield return resolution.Result;
            yield break;
        }

        if (resolution?.IsPlaylist is true)
        {
            await foreach (var track in api.SearchAsync(resolution.Query, cancellationToken)) yield return track;
            yield break;
        }

        // Ordinary text, or a resolver that could not answer. Search streams its hits in the order
        // the pods answer, not by relevance, so every hit has to be in before one can be picked.
        var results = new List<Track>();
        await foreach (var track in api.SearchAsync(resolution?.Query ?? search, cancellationToken)) results.Add(track);

        var best = BestMatch(results, search);
        if (best is not null) yield return best;
    }

    /// <summary>
    /// The closest hit by edit distance against the title and against the title and artist in
    /// either order, with the platform as the tie-break: the library, then Deezer, then YouTube.
    /// </summary>
    /// <remarks>
    /// The artist on its own is deliberately not a candidate. Scoring it would let every track by
    /// the right artist tie with the one that was actually asked for, and "rammstein sonne" would
    /// come back with whichever Rammstein track a pod happened to answer with first.
    /// </remarks>
    internal static Track? BestMatch(List<Track> results, string search)
    {
        var term = search.Trim();

        return results
            .OrderBy(track => Math.Min(
                LevenshteinDistance.ComputeLean(track.Name, term),
                Math.Min(LevenshteinDistance.ComputeLean(track.DisplayName, term),
                    LevenshteinDistance.ComputeLean($"{track.Artist} - {track.Name}", term))))
            .ThenBy(track => track.Id switch
            {
                _ when track.Id.StartsWith("audio://", StringComparison.Ordinal) => 0,
                _ when track.Id.StartsWith("deezer://", StringComparison.Ordinal) => 1,
                _ => 2
            })
            .FirstOrDefault();
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
            player.Connection = await player.VoiceChannel!.ConnectAsync();
            _ = Task.Run(() => player.PlayAsync());
        }
        catch (Exception e)
        {
            logger.Error(e, "Connecting to {Channel} failed", player.VoiceChannel?.Name);
            player.Started = false;
            controller.Remove(player);
            await RefuseAsync(ctx, Text.DiscordDidTheFunny());
        }
    }

    /// <summary>The two guards every command starts with: user in a channel, bot in that channel.</summary>
    private async ValueTask<Player?> PlayerForAsync(CommandContext ctx, string command)
    {
        var voice = await UserVoiceChannelAsync(ctx, command);
        if (voice is null) return null;

        var player = controller.GetPlayer(voice);
        if (player is not null) return player;

        await RefuseAsync(ctx, Text.BotIsNotInTheChannel());
        return null;
    }

    private static async ValueTask<DiscordChannel?> UserVoiceChannelAsync(CommandContext ctx, string command)
    {
        var channelId = ctx.Member?.VoiceState?.ChannelId;

        if (channelId is not null && ctx.Guild is not null && ctx.Guild.Channels.TryGetValue(channelId.Value, out var channel))
        {
            return channel;
        }

        await RefuseAsync(ctx, Text.EnterChannelBeforeCommand(command));
        return null;
    }

    /// <summary>
    /// A message with no player behind it: it comes from the account that received the command,
    /// which is the master. Text commands keep the old bot's habit of answering in a DM.
    /// </summary>
    private static async ValueTask RefuseAsync(CommandContext ctx, string text)
    {
        if (ctx is SlashCommandContext)
        {
            await ctx.RespondAsync(text.CodeBlocked());
            return;
        }

        try
        {
            if (ctx.Member is not null)
            {
                await ctx.Member.SendMessageAsync(text.CodeBlocked());
                return;
            }
        }
        catch
        {
            // Closed DMs; fall through and say it in the channel.
        }

        await ctx.RespondAsync(text.CodeBlocked());
    }

    /// <summary>
    /// A message about a player: it comes from that player's own account, so a guild running
    /// several players spreads its rate limits. A slash command must be answered by the account
    /// that received it, so that one answers for itself.
    /// </summary>
    private static async ValueTask AcknowledgeAsync(CommandContext ctx, Player? player, string text)
    {
        if (ctx is SlashCommandContext || player?.Channel is null)
        {
            await ctx.RespondAsync(text.CodeBlocked());
            return;
        }

        await player.Channel.SendMessageAsync(text.CodeBlocked());
    }

    private static string Truncate(string value, int length) =>
        string.IsNullOrEmpty(value) ? "—" : value.Length <= length ? value : value[..length];
}
