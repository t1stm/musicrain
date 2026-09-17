using DSharpPlus;
using DSharpPlus.Entities;
using Gaida.Bot.Admin;
using Gaida.Bot.Gaida;
using Serilog;

namespace Gaida.Bot.Players;

/// <summary>
/// The old <c>Bot/Audio/Manager.cs</c> registry: every live player, and which account serves the
/// next request. The master account is the only one that listens for commands; the others exist to
/// be bodies in voice channels, each owning the statusbar for the player it is running.
/// </summary>
public sealed class PlayerController(GaidaClient api, ILogger logger, BotEventLog events)
{
    private readonly List<Player> _players = [];

    /// <summary>Every connected account, master first — which is also the allocation order.</summary>
    public List<DiscordClient> Clients { get; } = [];

    public DiscordClient? Master => Clients.FirstOrDefault();

    /// <summary>Every live player, for the admin snapshot.</summary>
    public IReadOnlyList<Player> Players
    {
        get
        {
            lock (_players) return [.. _players];
        }
    }

    /// <summary>What the bot did and was asked to do, for the admin snapshot.</summary>
    public BotEventLog Events => events;

    public void Register(DiscordClient client)
    {
        lock (Clients) Clients.Add(client);
    }

    /// <summary>
    /// The player for a voice channel, or a new one on a free account. <paramref name="voiceChannel" />
    /// and <paramref name="textChannel" /> come from whichever client received the command; the
    /// player is given the chosen account's own objects for the same channels.
    /// </summary>
    public Player? GetPlayer(DiscordChannel voiceChannel, DiscordChannel? textChannel = null, bool generateNew = false)
    {
        lock (_players)
        {
            var existing = _players.FirstOrDefault(player => player.VoiceChannel?.Id == voiceChannel.Id);
            if (existing is not null) return existing;

            if (!generateNew) return null;

            var guildId = voiceChannel.Guild.Id;

            foreach (var client in Clients)
            {
                if (_players.Any(player => player.Client == client && player.Guild?.Id == guildId)) continue;

                if (!client.Guilds.TryGetValue(guildId, out var guild)) continue;
                if (!guild.Channels.TryGetValue(voiceChannel.Id, out var ownVoiceChannel)) continue;

                var ownTextChannel = textChannel is not null && guild.Channels.TryGetValue(textChannel.Id, out var found)
                    ? found
                    : null;

                var player = new Player
                {
                    Client = client,
                    Api = api,
                    Logger = logger,
                    Controller = this,
                    Guild = guild,
                    VoiceChannel = ownVoiceChannel,
                    Channel = ownTextChannel
                };

                _players.Add(player);
                logger.Information("{Account} is taking {Channel} in {Guild}", client.CurrentUser.Username,
                    ownVoiceChannel.Name, guild.Name);
                events.Record("join", client.CurrentUser.Username, guild.Name, ownVoiceChannel.Name,
                    detail: _players.Count(other => other.Guild?.Id == guildId) > 1
                        ? "joined; another account was already playing in this guild"
                        : "joined");

                return player;
            }

            logger.Information("No free accounts left in {Guild}", voiceChannel.Guild.Name);
            events.Record("refused", Master?.CurrentUser.Username ?? "—", voiceChannel.Guild.Name,
                voiceChannel.Name, detail: $"no free accounts, all {Clients.Count} are busy here");
            return null;
        }
    }

    /// <summary>The player a button click belongs to: same text channel, clicker in its voice channel.</summary>
    public Player? GetPlayerForInteraction(ulong textChannelId, DiscordUser user)
    {
        lock (_players)
        {
            return _players.FirstOrDefault(player =>
                player.Channel?.Id == textChannelId && player.VoiceUsers.Any(member => member.Id == user.Id));
        }
    }

    /// <summary>The player whose voice channel a voice state change concerns, for one account.</summary>
    public Player? GetPlayerOf(DiscordClient client, ulong guildId)
    {
        lock (_players)
        {
            return _players.FirstOrDefault(player => player.Client == client && player.Guild?.Id == guildId);
        }
    }

    public void Remove(Player player)
    {
        lock (_players) _players.Remove(player);
    }
}
