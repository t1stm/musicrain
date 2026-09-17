using System.Globalization;
using System.Text.Json;
using DSharpPlus;
using DSharpPlus.Commands;
using DSharpPlus.Commands.Processors.SlashCommands;
using DSharpPlus.Commands.Processors.TextCommands;
using DSharpPlus.Commands.Processors.TextCommands.Parsing;
using DSharpPlus.Interactivity.Extensions;
using DSharpPlus.Voice;
using Gaida.Bot;
using Gaida.Bot.Admin;
using Gaida.Bot.Commands;
using Gaida.Bot.Gaida;
using Gaida.Bot.Players;
using Gaida.Admin;
using Serilog;

var logger = new LoggerConfiguration()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .CreateLogger();

// Whitespace counts as unset: compose passes BOT_CONFIGURATION through as ${BOT_CONFIGURATION:-},
// so an empty variable exists rather than being absent, and an empty string that merely is not null
// would win over the mounted file and then fail to parse.
var botInlineConfiguration = Environment.GetEnvironmentVariable("BOT_CONFIGURATION") is { } inline
                             && !string.IsNullOrWhiteSpace(inline)
    ? inline
    : null;
var botConfigurationLocation = Environment.GetEnvironmentVariable("CONFIGURATION_LOCATION") ?? ".env.json";

BotParametersConfiguration[]? botsConfig;

if (botInlineConfiguration is null && !File.Exists(botConfigurationLocation))
{
    // The state of a fresh checkout, and of the stack brought up without a token. Not an error
    // worth a stack trace, and not something to crash-loop over under a restart policy.
    logger.Fatal("No bot accounts configured: set BOT_CONFIGURATION, or put a file at {Location}",
        botConfigurationLocation);
    return;
}

try
{
    botsConfig = JsonSerializer.Deserialize<BotParametersConfiguration[]>(
        botInlineConfiguration ?? File.ReadAllText(botConfigurationLocation), JsonSerializerOptions.Web);
}
catch (Exception e)
{
    // No tokens is the ordinary state of a fresh checkout, so say what is missing and stop rather
    // than dying on an unhandled exception and crash-looping under a restart policy.
    logger.Fatal(e, "Could not read the bot configuration from {Source}",
        botInlineConfiguration is not null ? "BOT_CONFIGURATION" : botConfigurationLocation);
    return;
}

if (botsConfig is null || botsConfig.Length == 0)
{
    logger.Fatal("Failed to read the bot configuration from {Source}",
        botInlineConfiguration is not null ? "BOT_CONFIGURATION" : botConfigurationLocation);
    return;
}

// The master account is the only one that listens for commands; the rest are bodies for voice
// channels it cannot serve. Ordering the configuration master-first also orders the allocation.
var masters = botsConfig.Where(bot => bot.Master).ToArray();
if (masters.Length > 1)
{
    logger.Warning("{Count} accounts are marked as master. {Name} is the master; the rest are not",
        masters.Length, masters[0].Name);
}

var master = masters.FirstOrDefault() ?? botsConfig[0];
var bots = botsConfig.OrderByDescending(bot => bot == master).ToArray();

// Shared by every account: one registry of players, one client for the running Gaida instance, and
// one audit trail of everything the bot does, which Oko reads out of /Admin/snapshot.
var api = new GaidaClient(logger);
var events = new BotEventLog();
var controller = new PlayerController(api, logger, events);

logger.Information("Talking to the Gaida instance at {BaseUrl}", api.BaseUrl);

foreach (var config in bots)
{
    if (config.Token is null)
    {
        logger.Fatal("Token is missing from configuration for {Name}", config.Name);
        return;
    }

    var isMaster = config == master;

    if (!isMaster && config.Prefixes.Length > 0)
    {
        logger.Warning("{Name} is not the master account, so its prefixes are ignored", config.Name);
    }

    // Every client builds its own service provider, so the shared services are registered as
    // instances: registering the types would hand each account a registry of its own.
    var services = BotServices.Register(new ServiceCollection(), logger, api, controller);

    var builder = DiscordClientBuilder
        .CreateDefault(config.Token,
            DiscordIntents.AllUnprivileged | DiscordIntents.MessageContents | TextCommandProcessor.RequiredIntents,
            services)
        .UseVoice()
        .UseInteractivity()
        .ConfigureEventHandlers(handlers => handlers
            .HandleComponentInteractionCreated((client, args) =>
                ButtonHandler.HandleAsync(controller, logger, client, args))
            .HandleVoiceStateUpdated(async (client, args) =>
            {
                try
                {
                    if (args.UserId != client.CurrentUser.Id || args.GuildId is null) return;

                    var player = controller.GetPlayerOf(client, args.GuildId.Value);
                    if (player is null) return;

                    if (args.After.ChannelId is null)
                    {
                        logger.Information("{Account} was removed from voice in {Guild}",
                            client.CurrentUser.Username, player.Guild?.Name);
                        player.Record("kicked", "was disconnected from the voice channel");
                        await player.DisconnectAsync();
                        return;
                    }

                    if (args.Before.ChannelId == args.After.ChannelId) return;
                    if (!client.Guilds.TryGetValue(args.GuildId.Value, out var guild)) return;
                    if (!guild.Channels.TryGetValue(args.After.ChannelId.Value, out var channel)) return;

                    // DSharpPlus.Voice moves the connection itself; the player only follows.
                    player.MovedTo(channel);
                }
                catch (Exception e)
                {
                    logger.Error(e, "Handling a voice state update failed");
                }
            }));

    if (isMaster)
    {
        builder.UseCommands((_, commandsExtension) =>
        {
            commandsExtension.AddProcessor(new TextCommandProcessor
            {
                Configuration = new TextCommandConfiguration
                {
                    PrefixResolver = new DefaultPrefixResolver(true, config.Prefixes).ResolvePrefixAsync
                }
            });
            commandsExtension.AddProcessor<SlashCommandProcessor>();
            commandsExtension.AddCommands<PlaybackCommands>();

            // One hook for all fifteen commands, rather than a line of bookkeeping in each.
            commandsExtension.CommandExecuted += (_, args) =>
            {
                events.Record("command", args.Context.Client.CurrentUser.Username, args.Context.Guild?.Name,
                    args.Context.Channel.Name, args.Context.User.Username, args.Context.Command.Name);
                return Task.CompletedTask;
            };

            commandsExtension.CommandErrored += (_, args) =>
            {
                events.Record("command-failed", args.Context.Client.CurrentUser.Username, args.Context.Guild?.Name,
                    args.Context.Channel.Name, args.Context.User.Username,
                    $"{args.Context.Command.Name}: {args.Exception.Message}");
                return Task.CompletedTask;
            };
        });
    }

    var client = builder.Build();

    try
    {
        await client.ConnectAsync();
    }
    catch (Exception e)
    {
        // A dead secondary is one fewer body for voice channels, not a reason to take the bot down.
        // A dead master means nothing would listen for commands at all.
        if (isMaster)
        {
            logger.Fatal(e, "The master account {Name} could not connect", config.Name);
            return;
        }

        logger.Error(e, "{Name} could not connect and is being skipped", config.Name);
        continue;
    }

    controller.Register(client);
    logger.Information("{Name} connected as {Account}{Role}", config.Name, client.CurrentUser.Username,
        isMaster ? " (master)" : "");
    events.Record("connected", client.CurrentUser.Username,
        detail: isMaster ? "connected as the master account" : "connected");
}

if (controller.Clients.Count == 0)
{
    logger.Fatal("No accounts connected");
    return;
}

// The bot serves nothing of its own, but Oko watches by pulling, so it needs somewhere to pull from:
// /Admin/snapshot, /Admin/requests and /Admin/events, behind the same ADMIN_TOKEN as every other
// service. Without the token MapAdmin maps nothing at all and this is an idle host on a closed port.
var admin = WebApplication.CreateBuilder();
admin.WebHost.UseUrls(Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://+:8080");
admin.Logging.ClearProviders();

var app = admin.Build();
var masterNames = controller.Clients.Take(1).Select(client => client.CurrentUser.Username).ToArray();

app.MapAdmin(() => BotSnapshot.Build(controller, events, masterNames, api.BaseUrl));

await app.RunAsync();
