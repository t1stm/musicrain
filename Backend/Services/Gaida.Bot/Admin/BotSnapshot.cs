using System.Globalization;
using DSharpPlus;
using Gaida.Bot.Messages;
using Gaida.Bot.Players;

namespace Gaida.Bot.Admin;

/// <summary>What an operator sees in Oko: who is connected, what is playing, and what has happened.</summary>
/// <remarks>
///     Built per request and never cached — the bot's own state is the only copy, and a second one
///     would just be staler. Tokens are not in it: an account is identified by its configured name and
///     its Discord username, both of which are already public.
/// </remarks>
public static class BotSnapshot
{
    public static object Build(PlayerController controller, BotEventLog events, IReadOnlyList<string> masters,
        string apiBaseUrl, IReadOnlyDictionary<string, DiscordClient> accounts, BotStatusStore statuses)
    {
        var players = controller.Players;

        return new
        {
            service = "gaida-bot",
            api = apiBaseUrl,
            // Over the key map rather than the controller's clients: same clients, same order, but
            // these carry the configured name /Admin/set-status is addressed by.
            accounts = accounts.Select(account =>
                Account(account.Key, account.Value, players, masters, statuses.For(account.Key))),
            players = players.Select(Player),
            events = events.Recent()
        };
    }

    private static object Account(string name, DiscordClient client, IReadOnlyList<Player> players,
        IReadOnlyList<string> masters, BotStatusEntry? status)
    {
        return new
        {
            name,
            username = client.CurrentUser.Username,
            id = client.CurrentUser.Id.ToString(CultureInfo.InvariantCulture),
            master = masters.Contains(client.CurrentUser.Username),
            guilds = client.Guilds.Count,
            playing = players.Count(player => player.Client == client),
            // Null until an operator sets one: the account is online with no activity, as it was
            // before any of this existed.
            status
        };
    }

    private static object Player(Player player)
    {
        var current = player.CurrentItem;

        return new
        {
            account = player.Client.CurrentUser.Username,
            guild = player.Guild?.Name,
            channel = player.VoiceChannel?.Name,
            listeners = player.VoiceUsers.Count(member => !member.IsBot),
            track = current?.DisplayName,
            kind = current?.Kind,
            requester = current?.Requester?.Username,
            currentIndex = player.Queue.Current,
            queueLength = player.Queue.Count,
            paused = player.Paused,
            loop = player.LoopStatus.ToString(),
            elapsed = Statusbar.Time(player.Stopwatch.Elapsed),
            length = current is null || current.Length == TimeSpan.Zero ? null : Statusbar.Time(current.Length),
            bitrate = player.Bitrate
        };
    }
}
