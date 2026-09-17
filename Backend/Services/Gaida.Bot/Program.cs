using System.Globalization;
using System.Text.Json;
using DSharpPlus;
using DSharpPlus.Commands;
using DSharpPlus.Commands.Processors.SlashCommands;
using DSharpPlus.Commands.Processors.TextCommands;
using DSharpPlus.Commands.Processors.TextCommands.Parsing;
using DSharpPlus.Entities;
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

// The one thing the bot writes down. Same shape as CONFIGURATION_LOCATION: a path, defaulted for
// `dotnet run` and pointed at a volume in compose. Read once here and applied at connect, so an
// account never flashes as plain Online on the way up.
var statuses = new BotStatusStore(Environment.GetEnvironmentVariable("STATUS_LOCATION") ?? ".status.json", logger);

// Which account is which, for /Admin/set-status. Keyed the way the statuses are — by the configured
// name, which is stable across a rename in the developer portal and known before the account connects.
var accounts = new Dictionary<string, DiscordClient>();

logger.Information("Talking to the Gaida instance at {BaseUrl}", api.BaseUrl);

foreach (var (index, config) in bots.Index())
{
    if (config.Token is null)
    {
        logger.Fatal("Token is missing from configuration for {Name}", config.Name);
        return;
    }

    var isMaster = config == master;
    // A name of its own where it has a unique one, and name#index where it does not: two accounts
    // sharing a key would collide in the map below, and the second would vanish from the panel.
    var key = config.Name is not null && bots.Count(other => other.Name == config.Name) == 1
        ? config.Name
        : $"{config.Name}#{index}";

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
        // The overload that carries the presence in the IDENTIFY, rather than a second round-trip
        // after it — a restart brings the account back already wearing what the operator set.
        var (activity, presence) = BotStatusStore.Build(statuses.For(key));
        await client.ConnectAsync(activity!, presence);
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
    accounts[key] = client;
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

var adminApi = app.MapAdmin(() => BotSnapshot.Build(controller, events, masterNames, api.BaseUrl, accounts, statuses));

// The bot's only mutations. Named operations with a reason on every rejection, like Dom's — Oko
// forwards a query string and knows nothing about what any of it means.
if (adminApi is not null)
{
    adminApi.MapPost("/set-status", async Task<IResult> (string account, string presence,
        string? activity, string? text, string? url) =>
    {
        if (!accounts.TryGetValue(account, out var client)) return Results.NotFound();

        var (entry, error) = BotStatusStore.Parse(presence, activity, text, url);
        if (entry is null) return Results.BadRequest(new { error });

        // Applied first, remembered second: a gateway that refuses the update leaves nothing written
        // for the next restart to put back.
        var (discordActivity, discordPresence) = BotStatusStore.Build(entry);
        await client.UpdateStatusAsync(discordActivity!, discordPresence);
        statuses.Set(account, entry);

        events.Record("status", client.CurrentUser.Username,
            detail: entry.Activity is null ? entry.Presence : $"{entry.Presence}, {entry.Activity} {entry.Text}");

        return Results.Ok(entry);
    });

    adminApi.MapPost("/clear-status", async Task<IResult> (string account) =>
    {
        if (!accounts.TryGetValue(account, out var client)) return Results.NotFound();

        await client.UpdateStatusAsync(null!, DiscordUserStatus.Online);
        statuses.Clear(account);
        events.Record("status", client.CurrentUser.Username, detail: "cleared");

        return Results.Ok();
    });
}

await app.RunAsync();
