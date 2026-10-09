using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dom.Store;
using Serilog.Core;

namespace Gaida.Tests;

/// <summary>
///     Every kind of change, then a restart: what comes back from the database has to be exactly what
///     was in memory. A change that forgot to write what it touched shows up here and nowhere else —
///     until the next deploy signs somebody out or loses a playlist.
/// </summary>
public class DomPersistenceTests : IDisposable
{
    private const string Password = "correct horse battery";

    private readonly string _directory = Directory.CreateTempSubdirectory("dom-persistence").FullName;

    public void Dispose() => Directory.Delete(_directory, true);

    [Fact]
    public void EveryChangeSurvivesARestart()
    {
        var store = Store();

        // Compared after every step, not once at the end: a later change that happens to rewrite the same
        // account would otherwise hide an earlier one that forgot to.
        void Step(Action change)
        {
            change();
            Assert.Equal(State(store), State(Store()));
        }

        User kris = null!, ana = null!, mila = null!, gone = null!;
        Playlist mix = null!, theirs = null!, removed = null!;

        Step(() => kris = User(store, "kris"));
        Step(() => ana = User(store, "ana"));
        Step(() => mila = User(store, "mila"));
        Step(() => gone = User(store, "gone"));
        Step(() => store.Login("kris", Password));
        Step(() => store.Logout(store.Login("ana", Password).token!.Value));

        Step(() => Befriend(store, kris, ana));
        Step(() => Befriend(store, kris, mila));
        Step(() => Befriend(store, kris, gone));
        Step(() => Befriend(store, ana, gone));

        Step(() => mix = store.Create(kris, "Mix", Visibility.Friends, [Track("yt://1"), Track("yt://2")]).playlist!);
        Step(() => store.Update(kris, mix.Id, "Mix two", null, null, ["ana", "mila", "gone"]));
        Step(() => store.Append(ana, mix.Id, Track("yt://3")));
        Step(() => store.Update(mila, mix.Id, null, null, [Track("yt://3"), Track("yt://1"), Track("yt://4")],
            revision: 2));
        Step(() => store.SetCover(kris, mix.Id, "cover.webp"));

        Step(() => theirs = store.Create(ana, "Theirs", Visibility.Public, [Track("yt://5")]).playlist!);
        Step(() => store.Update(ana, theirs.Id, null, null, null, ["kris"]));
        Step(() => store.Append(kris, theirs.Id, Track("yt://6")));
        Step(() => store.Create(gone, "Doomed", Visibility.Public, []));
        Step(() => removed = store.Create(kris, "Removed", Visibility.Private, []).playlist!);
        Step(() => store.Delete(kris, removed.Id));

        Step(() => store.MergeSettings(kris,
            new JsonObject { ["theme"] = "dark", ["quality"] = new JsonObject { ["codec"] = "FLAC" } }));
        Step(() => store.MergeSettings(kris, new JsonObject { ["theme"] = null, ["volume"] = 0.5 }));

        // a rename that reaches friends, a playlist it owns, one it collaborates on and a track it added
        Step(() => Assert.Null(store.Rename(kris, Password, "Kristian").error));
        Step(() => Assert.Null(store.Rename(mila, Password, "MILA").error));
        Step(() => Assert.True(store.Unfriend(ana, "Kristian")));
        Step(() => Assert.Null(store.DeleteAccount(gone, Password).error));
        Step(() => Assert.Null(store.ChangePassword(ana, Password, "another password").error));
        Step(() => Assert.Equal(2, store.SignOutEverywhere(kris, "none of them")));

        Step(() => Assert.True(store.AdminUpdatePlaylist(theirs.Id, "Renamed by an operator", false, 0).ok));
        Step(() => Assert.True(store.AdminSignOut("MILA").ok));
        Step(() => Assert.True(store.AdminSetPassword("ana", "a third password").ok));
        Step(() => Assert.True(store.AdminRenameUser("ana", "Anna").ok));
        Step(() => Assert.True(store.AdminDeletePlaylist(mix.Id).ok));
        Step(() => Assert.True(store.AdminDeleteUser("MILA").ok));

        var after = State(store);
        Assert.Contains("Kristian", after);
        Assert.DoesNotContain("MILA", after);
    }

    /// <summary>The live accounts and playlists, as the old file would have held them, in a stable order.</summary>
    private static string State(DomStore store)
    {
        static T Field<T>(DomStore store, string name) =>
            (T)typeof(DomStore).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;

        var state = new DomState
        {
            Users = [.. Field<Dictionary<string, User>>(store, "_users").Values.OrderBy(user => user.Key)],
            Playlists = [.. Field<Dictionary<string, Playlist>>(store, "_playlists").Values.OrderBy(playlist => playlist.Id)]
        };

        return JsonSerializer.Serialize(state);
    }

    private static void Befriend(DomStore store, User inviter, User friend) =>
        Assert.Null(store.AcceptInvite(friend, store.OpenInvite(inviter)!.Code).error);

    private static User User(DomStore store, string username) =>
        store.Resolve(store.Register(username, Password).token!.Value)!;

    private static TrackSnapshot Track(string id) =>
        new() { Id = id, Name = "Nightcall", Artist = "Kavinsky", Album = "OutRun", Duration = "00:04:18" };

    private DomStore Store() => new(Path.Combine(_directory, "dom.db"), Logger.None);
}
