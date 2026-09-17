namespace Gaida.Bot.Admin;

/// <summary>One thing the bot did, or was asked to do.</summary>
/// <param name="Account">The bot account it happened to — a guild can be running several.</param>
/// <param name="User">Who asked, where somebody did.</param>
public sealed record BotEvent(
    DateTimeOffset At,
    string Kind,
    string Account,
    string? Guild,
    string? Channel,
    string? User,
    string Detail);

/// <summary>
///     The bot's audit trail: joins, moves, departures, track changes, commands and button presses,
///     newest last. Oko has an audit log of its own, but that one records what an operator changed
///     through the panel — nothing a watched service can write into — so the bot keeps its own and
///     hands it over in its snapshot.
/// </summary>
/// <remarks>
///     ponytail: in memory, capped at <see cref="Capacity" />, gone on restart, exactly like Oko's own
///     log and the request ring in Gaida.Admin. It is bounded by construction, so an unwatched bot
///     costs nothing. Make it a file the day the record has to outlive the process.
/// </remarks>
public sealed class BotEventLog
{
    private const int Capacity = 500;

    private readonly Lock _gate = new();
    private readonly Queue<BotEvent> _entries = new(Capacity);

    public void Record(string kind, string account, string? guild = null, string? channel = null,
        string? user = null, string detail = "")
    {
        lock (_gate)
        {
            if (_entries.Count == Capacity) _entries.Dequeue();
            _entries.Enqueue(new BotEvent(DateTimeOffset.UtcNow, kind, account, guild, channel, user, detail));
        }
    }

    public BotEvent[] Recent()
    {
        lock (_gate) return [.. _entries];
    }
}
