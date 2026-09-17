using DSharpPlus.Entities;
using Gaida.Bot.Admin;
using Serilog;

namespace Gaida.Tests;

/// <summary>
/// The operator-set Discord status, minus Discord: what <see cref="BotStatusStore.Parse" /> accepts
/// and refuses, what <see cref="BotStatusStore.Build" /> hands the gateway, and whether the file
/// survives a restart — which is the whole point of there being a file.
/// </summary>
public class BotStatusTests
{
    private static readonly ILogger Quiet = new LoggerConfiguration().CreateLogger();

    [Fact]
    public void ParseAcceptsAPresenceWithAnActivity()
    {
        var (entry, error) = BotStatusStore.Parse("donotdisturb", "listeningto", " the library ", null);

        Assert.Null(error);
        Assert.Equal("DoNotDisturb", entry!.Presence);
        Assert.Equal("ListeningTo", entry.Activity);
        Assert.Equal("the library", entry.Text);
    }

    [Fact]
    public void ParseAcceptsAPresenceWithoutOne()
    {
        var (entry, error) = BotStatusStore.Parse("idle", "none", null, null);

        Assert.Null(error);
        Assert.Equal("Idle", entry!.Presence);
        Assert.Null(entry.Activity);
    }

    [Theory]
    [InlineData("busy", "playing", "music", null)] // not a presence
    [InlineData("online", "vibing", "music", null)] // not an activity
    [InlineData("online", "playing", null, null)] // an activity with no name renders as nothing
    [InlineData("online", "none", "music", null)] // text with nothing to attach it to
    [InlineData("online", "custom", "music", null)] // not settable through DSharpPlus
    [InlineData("online", "streaming", "music", null)] // streaming with no URL is silently downgraded
    [InlineData("online", "streaming", "music", "https://example.com/live")] // not a stream host
    [InlineData("online", "streaming", "music", "https://eviltwitch.tv/x")] // suffix, not the domain
    [InlineData("online", "playing", "music", "https://twitch.tv/x")] // a URL nothing would use
    public void ParseRefuses(string presence, string activity, string? text, string? url)
    {
        var (entry, error) = BotStatusStore.Parse(presence, activity, text, url);

        Assert.Null(entry);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void ParseRefusesTextLongerThanDiscordAllows()
    {
        var (entry, error) = BotStatusStore.Parse("online", "playing", new string('x', 129), null);

        Assert.Null(entry);
        Assert.Contains("128", error);
    }

    [Fact]
    public void BuildIsAnOrdinaryOnlineBotWhenNothingWasSet()
    {
        var (activity, presence) = BotStatusStore.Build(null);

        Assert.Null(activity);
        Assert.Equal(DiscordUserStatus.Online, presence);
    }

    [Fact]
    public void BuildCarriesTheStreamUrl()
    {
        var (entry, _) = BotStatusStore.Parse("online", "streaming", "a set", "https://www.twitch.tv/x");
        var (activity, presence) = BotStatusStore.Build(entry);

        Assert.Equal(DiscordUserStatus.Online, presence);
        Assert.Equal(DiscordActivityType.Streaming, activity!.ActivityType);
        Assert.Equal("a set", activity.Name);
        Assert.Equal("https://www.twitch.tv/x", activity.StreamUrl);
    }

    [Fact]
    public void SetSurvivesAReload()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            var store = new BotStatusStore(path, Quiet);
            store.Set("gaida", new BotStatusEntry("Idle", "Watching", "the queue", null));

            var reloaded = new BotStatusStore(path, Quiet);
            Assert.Equal("the queue", reloaded.For("gaida")!.Text);

            reloaded.Clear("gaida");
            Assert.Null(new BotStatusStore(path, Quiet).For("gaida"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AnUnreadableFileIsEmptyRatherThanFatal()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        File.WriteAllText(path, "{ not json");

        try
        {
            Assert.Null(new BotStatusStore(path, Quiet).For("gaida"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
