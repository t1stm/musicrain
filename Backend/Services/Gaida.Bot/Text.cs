using Gaida.Bot.Enums;

namespace Gaida.Bot;

/// <summary>
/// Every user-facing string, in English. The old bot carried an <c>ILanguage</c> with a Bulgarian
/// twin and a per-user choice in MariaDB; without the database there is nothing to choose with.
/// </summary>
public static class Text
{
    public const string Farewell = @"Bye! \(◕ ◡ ◕\)";

    public static string EnterChannelBeforeCommand(string command) =>
        $"Enter a channel before using the \"{command}\" command.";

    public static string NoFreeBotAccounts() =>
        "No free bot accounts in this guild. You can add more bot accounts from the bot's support server.";

    public static string ThisMessageWillUpdateShortly() => "Hello! This message will update shortly.";

    public static string SelectVideo() => "Select a video.";

    public static string SelectVideoTimeout() => "Time to select video ran out.";

    public static string NoResultsFound(string term) => $"No results could be found for the search term: \"{term}\"";

    public static string AddedItem(string term) => $"Added: {term}";

    public static string BotIsNotInTheChannel() => "The bot isn't in the channel.";

    public static string LoopStatusUpdate(LoopMode loop) => "Loop status is now: " + loop switch
    {
        LoopMode.None => "None",
        LoopMode.WholeQueue => "Looping whole queue.",
        LoopMode.One => "One Item Only.",
        _ => "None"
    };

    public static string NumberBiggerThanQueueLength(int number) =>
        $"Specified number: {number} is bigger than the Queue's Length. Searching the number instead.";

    public static string PlayingItemAfterThis(int index, string name) => $"Playing: ({index}) - \"{name}\" after this.";

    public static string PlayingItemAfterThis(string term) => $"Playing: \"{term}\" after this.";

    public static string FailedToRemove(string text) => $"Failed to remove: \"{text}\"";

    public static string RemovingItem(string name) => $"Removing \"{name}\"";

    public static string FailedToMove() => "Failed to move.";

    public static string Moved(int itemOne, string name, int itemTwo) => $"Moved ({itemOne}) \"{name}\" to ({itemTwo})";

    public static string InvalidMoveFormat() =>
        "Invalid move format.\n" +
        "You must use two numbers or use the format specified below:\n\n" +
        "-mv Exact Name !to Exact Name 2 ";

    public static string SwitchedThePlacesOf(string itemOne, string itemTwo) =>
        $"Switched the places of \"{itemOne}\" and \"{itemTwo}\"";

    public static string CurrentQueue() => "Current Queue:";

    public static string GoingTo(int index, string? thing) => $"Going to ({index}) - \"{thing}\"";

    public static string DiscordDidTheFunny() =>
        "Discord did the funny, so the bot tried to reconnect. If the playback stopped skip one time back and return to the current item.";

    public static string UserNotInChannelLyrics() =>
        "Enter a channel before using the lyrics command without a search term.";

    public static string BotNotInChannelLyrics() =>
        "The bot isn't in the channel. If you want to know the lyrics of a song add it's name after the command.";

    public static string NoResultsFoundLyrics(string search) => $"No results found for \"{search}\".";

    public static string LyricsLong() =>
        "The lyrics are longer than 2000 characters, which is Discord's length limit. Too bad. Sending song as a file.";

    public static string YouAreNotInTheChannel() => "You're not in the bot's current channel.";

    public static string ShufflingTheQueue() => "Shuffling the queue.";

    public static string SkippingOneTime() => "Skipping one time";

    public static string SkippingOneTimeBack() => "Skipping one time back.";

    public static string PausingThePlayer() => "Pausing the player.";

    public static string UnpausingThePlayer() => "Unpausing the player.";

    public static string Playing() => "Playing";

    public static string RequestedBy() => "Requested by";

    public static string NextUp() => "Next";

    public static string TheQueueIsEmpty() => "The queue is empty";

    public static string CodeBlocked(this string text) => $"```{text}```";
}
