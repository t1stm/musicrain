using System.Text.Json;
using DSharpPlus.Entities;
using JetBrains.Annotations;

namespace Gaida.Bot.Admin;

/// <summary>One account's operator-set presence, exactly as it is written to disk.</summary>
/// <remarks>
///     <paramref name="Presence" /> and <paramref name="Activity" /> are DSharpPlus enum names kept as
///     strings: the file stays readable, and a value Discord adds later costs no migration here.
///     A null <paramref name="Activity" /> is a presence with no activity line at all.
/// </remarks>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record BotStatusEntry(string Presence, string? Activity, string? Text, string? Url);

/// <summary>
///     What an operator set each account to, and the only state the bot keeps on disk.
/// </summary>
/// <remarks>
///     Keyed by the configured account name rather than the Discord username: it is stable across a
///     rename in the developer portal, and it is known before the account connects — which is what
///     lets the status ride the IDENTIFY instead of being a second round-trip after it.
/// </remarks>
public sealed class BotStatusStore
{
    /// <summary>Discord's limit on an activity name.</summary>
    private const int MaxTextLength = 128;

    private readonly Dictionary<string, BotStatusEntry> _entries;
    private readonly Lock _gate = new();
    private readonly ILogger _logger;
    private readonly string _path;

    public BotStatusStore(string path, ILogger logger)
    {
        _path = path;
        _logger = logger;
        _entries = Load(path, logger);
    }

    public BotStatusEntry? For(string account)
    {
        lock (_gate) return _entries.GetValueOrDefault(account);
    }

    public void Set(string account, BotStatusEntry entry)
    {
        lock (_gate)
        {
            _entries[account] = entry;
            Save();
        }
    }

    public void Clear(string account)
    {
        lock (_gate)
        {
            if (_entries.Remove(account)) Save();
        }
    }

    /// <summary>
    ///     What <c>ConnectAsync</c> and <c>UpdateStatusAsync</c> take. Nothing set — or nothing ever set
    ///     for this account — is an ordinary online bot with no activity, which is what it was before.
    /// </summary>
    public static (DiscordActivity? Activity, DiscordUserStatus Presence) Build(BotStatusEntry? entry)
    {
        if (entry is null) return (null, DiscordUserStatus.Online);

        var presence = Enum.Parse<DiscordUserStatus>(entry.Presence, true);
        if (entry.Activity is null) return (null, presence);

        var type = Enum.Parse<DiscordActivityType>(entry.Activity, true);
        var activity = new DiscordActivity(entry.Text ?? "", type);

        if (type == DiscordActivityType.Streaming && entry.Url is not null) activity.StreamUrl = entry.Url;

        return (activity, presence);
    }

    /// <summary>
    ///     Turns what Oko forwarded into an entry, or says why it will not. The whole validation surface:
    ///     Oko knows nothing about what these mean, so every rejection is written here.
    /// </summary>
    public static (BotStatusEntry? Entry, string? Error) Parse(string? presence, string? activity, string? text,
        string? url)
    {
        if (!Enum.TryParse<DiscordUserStatus>(presence, true, out var parsedPresence))
            return (null, $"unknown presence '{presence}'");

        text = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        url = string.IsNullOrWhiteSpace(url) ? null : url.Trim();

        // "none" as well as nothing at all: it is what the panel's activity picker sends for its first
        // option, and spelling it out beats an empty string in the query.
        if (string.IsNullOrWhiteSpace(activity) || activity.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            if (text is not null) return (null, "text needs an activity to go with");
            return (new BotStatusEntry(parsedPresence.ToString(), null, null, null), null);
        }

        if (!Enum.TryParse<DiscordActivityType>(activity, true, out var parsedActivity))
            return (null, $"unknown activity '{activity}'");

        // Discord reads a custom status from `state`, not from `name`, and this DSharpPlus exposes
        // DiscordActivity.CustomStatus as read-only with no public way to build one — so type 4 would
        // go out as a name Discord ignores and render as no status at all. Rejected here rather than
        // set and silently invisible. Revisit when the library makes the field writable.
        if (parsedActivity == DiscordActivityType.Custom)
            return (null, "custom statuses cannot be set: DSharpPlus has no writable state field for them");

        // An activity with no name renders as nothing at all, which looks exactly like a status that
        // failed to apply.
        if (text is null) return (null, "an activity needs text");
        if (text.Length > MaxTextLength) return (null, $"text is longer than {MaxTextLength} characters");

        if (parsedActivity == DiscordActivityType.Streaming)
        {
            // Discord quietly downgrades a stream with no recognised URL to Playing, and the operator is
            // left wondering why the type did not stick.
            if (url is null) return (null, "streaming needs a twitch.tv or youtube.com URL");
            if (!Uri.TryCreate(url, UriKind.Absolute, out var parsedUrl) || !IsStreamHost(parsedUrl))
                return (null, $"'{url}' is not a twitch.tv or youtube.com URL");
        }
        else if (url is not null)
        {
            return (null, "only streaming takes a URL");
        }

        return (new BotStatusEntry(parsedPresence.ToString(), parsedActivity.ToString(), text, url), null);
    }

    private static bool IsStreamHost(Uri url)
    {
        return Matches("twitch.tv") || Matches("youtube.com");

        // The domain itself or a subdomain of it, rather than a suffix match: "eviltwitch.tv" ends
        // with "twitch.tv" and is not Twitch.
        bool Matches(string domain) =>
            url.Host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
            url.Host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, BotStatusEntry> Load(string path, ILogger logger)
    {
        // A bot that cannot read its last status still has to connect, so every failure here is a
        // warning and an empty dictionary: the accounts come up plain Online, as they did before.
        try
        {
            if (!File.Exists(path)) return [];

            return JsonSerializer.Deserialize<Dictionary<string, BotStatusEntry>>(
                File.ReadAllText(path), JsonSerializerOptions.Web) ?? [];
        }
        catch (Exception e)
        {
            logger.Warning(e, "Could not read the saved statuses from {Path}", path);
            return [];
        }
    }

    // ponytail: the whole file is rewritten on every change. It is a handful of accounts, changed by
    // hand, by one operator. Make it atomic the day something else writes here.
    private void Save()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(_entries, JsonSerializerOptions.Web));
        }
        catch (Exception e)
        {
            // The status is already live on the account; only its survival of a restart is lost.
            _logger.Warning(e, "Could not save the statuses to {Path}", _path);
        }
    }
}
