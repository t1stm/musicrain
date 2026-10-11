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

    public const string NoFreeBotAccounts =
        "No free bot accounts in this guild. You can add more bot accounts from the bot's support server.";

    public const string ThisMessageWillUpdateShortly = "Hello! This message will update shortly.";

    public const string SelectVideo = "Select a video.";

    public const string SelectVideoTimeout = "Time to select video ran out.";

    public static string NoResultsFound(string term) => $"No results could be found for the search term: \"{term}\"";

    public static string AddedItem(string term) => $"Added: {term}";

    public const string BotIsNotInTheChannel = "The bot isn't in the channel.";

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

    public const string FailedToMove = "Failed to move.";

    public static string Moved(int itemOne, string name, int itemTwo) => $"Moved ({itemOne}) \"{name}\" to ({itemTwo})";

    public const string InvalidMoveFormat =
        "Invalid move format.\n" +
        "You must use two numbers or use the format specified below:\n\n" +
        "-mv Exact Name !to Exact Name 2 ";

    public static string SwitchedThePlacesOf(string itemOne, string itemTwo) =>
        $"Switched the places of \"{itemOne}\" and \"{itemTwo}\"";

    public const string CurrentQueue = "Current Queue:";

    public static string GoingTo(int index, string? thing) => $"Going to ({index}) - \"{thing}\"";

    public const string DiscordDidTheFunny =
        "Discord did the funny, so the bot tried to reconnect. If the playback stopped skip one time back and return to the current item.";

    public const string UserNotInChannelLyrics =
        "Enter a channel before using the lyrics command without a search term.";

    public const string BotNotInChannelLyrics =
        "The bot isn't in the channel. If you want to know the lyrics of a song add it's name after the command.";

    public static string NoResultsFoundLyrics(string search) => $"No results found for \"{search}\".";

    public const string LyricsLong =
        "The lyrics are longer than 2000 characters, which is Discord's length limit. Too bad. Sending song as a file.";

    public const string YouAreNotInTheChannel = "You're not in the bot's current channel.";

    public const string ShufflingTheQueue = "Shuffling the queue.";

    public const string SkippingOneTime = "Skipping one time";

    public const string SkippingOneTimeBack = "Skipping one time back.";

    public const string PausingThePlayer = "Pausing the player.";

    public const string UnpausingThePlayer = "Unpausing the player.";

    public const string Playing = "Playing";

    public const string Loading = "Loading";

    public const string RequestedBy = "Requested by";

    public const string NextUp = "Next";

    public const string TheQueueIsEmpty = "The queue is empty";

    public static string CodeBlocked(this string text) => $"```{text}```";
}
