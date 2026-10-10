using DSharpPlus.Commands;
using DSharpPlus.Commands.Processors.SlashCommands;
using DSharpPlus.Exceptions;
using Gaida.Bot.Players;

namespace Gaida.Bot.Commands;

/// <summary>Which account answers a command, and where.</summary>
internal static class Replies
{
    /// <summary>
    /// A message with no player behind it: it comes from the account that received the command,
    /// which is the master. Text commands keep the old bot's habit of answering in a DM.
    /// </summary>
    public static async ValueTask RefuseAsync(CommandContext ctx, string text)
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
        catch (DiscordException)
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
    public static async ValueTask AcknowledgeAsync(CommandContext ctx, Player? player, string text)
    {
        if (ctx is SlashCommandContext || player?.Channel is null)
        {
            await ctx.RespondAsync(text.CodeBlocked());
            return;
        }

        await player.Channel.SendMessageAsync(text.CodeBlocked());
    }
}
