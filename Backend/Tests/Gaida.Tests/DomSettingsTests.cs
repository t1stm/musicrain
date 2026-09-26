using System.Text.Json.Nodes;
using Dom.Store;
using Serilog.Core;

namespace Gaida.Tests;

/// <summary>
///     What the settings page asks of Dom: settings that merge rather than replace and survive a
///     restart, and the account changes that need the password as well as the token.
/// </summary>
public class DomSettingsTests : IDisposable
{
    private const string Password = "correct horse battery";

    private readonly string _directory = Directory.CreateTempSubdirectory("dom-settings").FullName;

    public void Dispose() => Directory.Delete(_directory, true);

    [Fact]
    public void AnAccountThatNeverSavedHasNoSettingsRatherThanEmptyOnes()
    {
        var store = Store();
        var (_, user, _, _) = store.Register("radost", Password);

        // the client seeds the account from the device on null, so null and {} must not blur
        Assert.Equal((null, null), store.Settings(user!));
    }

    [Fact]
    public void AMergeKeepsTheKeysItDidNotNameAndNullRemovesOne()
    {
        var store = Store();
        var (_, user, _, _) = store.Register("radost", Password);

        store.MergeSettings(user!, Patch("""{"quality":{"codec":"Opus","bitrate":192},"lyricsOpen":true}"""));
        store.MergeSettings(user!, Patch("""{"chatName":"Радост","lyricsOpen":null}"""));

        var (settings, updatedUtc) = store.Settings(user!);
        Assert.Equal("Opus", (string?)settings!["quality"]!["codec"]);
        Assert.Equal("Радост", (string?)settings["chatName"]);
        Assert.False(settings.ContainsKey("lyricsOpen"));
        Assert.NotNull(updatedUtc);
    }

    [Fact]
    public void SettingsSurviveARestart()
    {
        var first = Store();
        var (_, user, _, _) = first.Register("radost", Password);
        first.MergeSettings(user!, Patch("""{"quality":{"codec":"FLAC","bitrate":320}}"""));

        var reopened = Store();
        var again = reopened.Resolve(reopened.Login("radost", Password).token!.Value)!;

        Assert.Equal("FLAC", (string?)reopened.Settings(again).settings!["quality"]!["codec"]);
    }

    [Fact]
    public void AFileWrittenBeforeSettingsExistedLoadsWithNone()
    {
        Store().Register("radost", Password);

        // what the file looked like before this change: no settings properties at all
        var file = Path.Combine(_directory, "dom.json");
        var state = JsonNode.Parse(File.ReadAllText(file))!;
        var user = state["Users"]![0]!.AsObject();
        Assert.True(user.Remove("Settings") && user.Remove("SettingsUpdatedUtc"));
        File.WriteAllText(file, state.ToJsonString());

        var store = Store();
        var reloaded = store.Resolve(store.Login("radost", Password).token!.Value)!;

        Assert.Equal((null, null), store.Settings(reloaded));
    }

    [Fact]
    public void ChangingThePasswordSignsEveryOtherDeviceOutAndThisOneBackIn()
    {
        var store = Store();
        var (mine, user, _, _) = store.Register("radost", Password);
        var (other, _, _, _) = store.Login("radost", Password);

        var (fresh, error, _) = store.ChangePassword(user!, Password, "a different battery");

        Assert.Null(error);
        Assert.Null(store.Resolve(mine!.Value));
        Assert.Null(store.Resolve(other!.Value));
        Assert.Same(user, store.Resolve(fresh!.Value));
        Assert.NotNull(store.Login("radost", "a different battery").token);
        Assert.Equal("invalid_credentials", store.Login("radost", Password).error);
    }

    [Fact]
    public void AWrongCurrentPasswordChangesNothing()
    {
        var store = Store();
        var (mine, user, _, _) = store.Register("radost", Password);

        var (fresh, error, _) = store.ChangePassword(user!, "not it at all", "a different battery");

        Assert.Equal("invalid_credentials", error);
        Assert.Null(fresh);
        Assert.NotNull(store.Resolve(mine!.Value));
        Assert.NotNull(store.Login("radost", Password).token);
    }

    [Fact]
    public void SigningOutEverywhereKeepsOnlyTheCaller()
    {
        var store = Store();
        var (mine, user, _, _) = store.Register("radost", Password);
        var (phone, _, _, _) = store.Login("radost", Password);
        var (laptop, _, _, _) = store.Login("radost", Password);

        var revoked = store.SignOutEverywhere(user!, mine!.Value);

        Assert.Equal(2, revoked);
        Assert.NotNull(store.Resolve(mine.Value));
        Assert.Null(store.Resolve(phone!.Value));
        Assert.Null(store.Resolve(laptop!.Value));
    }

    [Fact]
    public void RenamingNeedsThePasswordAndCarriesThePlaylists()
    {
        var store = Store();
        var (_, user, _, _) = store.Register("radost", Password);
        store.Create(user!, "Late shift", false, []);

        Assert.Equal("invalid_credentials", store.Rename(user!, "not it at all", "boyan").error);
        Assert.Equal((null, null), store.Rename(user!, Password, "boyan"));

        Assert.Equal("boyan", user!.Username);
        Assert.Single(store.Mine(user));
        Assert.NotNull(store.Login("boyan", Password).token);
    }

    [Fact]
    public void DeletingNeedsThePasswordAndTakesThePlaylistsWithIt()
    {
        var store = Store();
        var (token, user, _, _) = store.Register("radost", Password);
        var (playlist, _, _) = store.Create(user!, "Late shift", true, []);
        store.SetCover(user!, playlist!.Id, "cover.png");

        Assert.Equal("invalid_credentials", store.DeleteAccount(user!, "not it at all").error);
        Assert.NotNull(store.Resolve(token!.Value));

        var (error, _, covers) = store.DeleteAccount(user!, Password);

        Assert.Null(error);
        Assert.Equal(["cover.png"], covers);
        Assert.Null(store.Resolve(token.Value));
        Assert.Empty(store.Public());
        Assert.Equal(0, store.UserCount);
    }

    [Fact]
    public void AnAccountDeletedMidRequestCannotBeRenamedBackIntoExistence()
    {
        var store = Store();
        var (_, user, _, _) = store.Register("radost", Password);
        store.DeleteAccount(user!, Password);

        Assert.Equal("unauthorized", store.Rename(user!, Password, "boyan").error);
        Assert.Equal("unauthorized", store.ChangePassword(user!, Password, "a different battery").error);
        Assert.Equal(0, store.UserCount);
    }

    private static JsonObject Patch(string json) => JsonNode.Parse(json)!.AsObject();

    private DomStore Store() => new(Path.Combine(_directory, "dom.json"), Logger.None);
}
