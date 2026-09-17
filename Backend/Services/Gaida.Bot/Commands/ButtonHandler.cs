using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;
using Gaida.Bot.Players;

namespace Gaida.Bot.Commands;

/// <summary>
/// The five statusbar buttons. Discord delivers a component interaction to the application that
/// owns the message, so every account handles its own statusbar — which is the point of running
/// several of them.
/// </summary>
public static class ButtonHandler
{
    public static async Task HandleAsync(PlayerController controller, ILogger logger, DiscordClient client,
        ComponentInteractionCreatedEventArgs args)
    {
        try
        {
            await args.Interaction.CreateResponseAsync(DiscordInteractionResponseType.DeferredMessageUpdate);

            var player = controller.GetPlayerForInteraction(args.Channel.Id, args.User);

            if (player is null)
            {
                await args.Interaction.CreateFollowupMessageAsync(new DiscordFollowupMessageBuilder()
                    .AsEphemeral()
                    .WithContent(Text.YouAreNotInTheChannel().CodeBlocked()));
                return;
            }

            player.Record("button", $"pressed {args.Id}", args.User.Username);

            switch (args.Id)
            {
                case "shuffle":
                    player.Shuffle();
                    await EphemeralAsync(args, Text.ShufflingTheQueue());
                    break;

                case "skip":
                    player.Skip();
                    await EphemeralAsync(args, Text.SkippingOneTime());
                    break;

                case "back":
                    player.Skip(-1);
                    await EphemeralAsync(args, Text.SkippingOneTimeBack());
                    break;

                case "pause":
                    player.Pause();
                    await EphemeralAsync(args, player.Paused ? Text.PausingThePlayer() : Text.UnpausingThePlayer());
                    break;

                case "leave":
                    await player.DisconnectAsync();
                    break;

                default:
                    logger.Debug("{Account} got an unknown component id {ID}", client.CurrentUser.Username, args.Id);
                    break;
            }
        }
        catch (Exception e)
        {
            logger.Warning(e, "Handling the {ID} button failed", args.Id);
        }
    }

    private static async Task EphemeralAsync(ComponentInteractionCreatedEventArgs args, string text) =>
        await args.Interaction.CreateFollowupMessageAsync(new DiscordFollowupMessageBuilder()
            .AsEphemeral()
            .WithContent(text.CodeBlocked()));
}
