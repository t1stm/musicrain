namespace Gaida.Bot;

public class BotParametersConfiguration
{
    /// <summary>
    /// The name of the bot that will be used in logging.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// The Discord Bot token for the current bot.
    /// </summary>
    public string? Token { get; set; }

    /// <summary>
    /// Message prefixes the bot responds to. Ignored unless this is the master account: a second
    /// account listening for the same prefix would answer every command twice.
    /// </summary>
    public string[] Prefixes { get; set; } = [];

    /// <summary>
    /// The account that listens for commands and is preferred when it is free. The others only join
    /// voice channels the master cannot serve, and each owns the statusbar of the player it runs.
    /// Exactly one account is the master; with none marked, the first configured one is.
    /// </summary>
    public bool Master { get; set; }
}
