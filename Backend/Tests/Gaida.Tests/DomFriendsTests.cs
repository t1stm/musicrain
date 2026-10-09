using Dom.Store;
using Serilog.Core;

namespace Gaida.Tests;

/// <summary>
///     Friends by code, the friends-only visibility, and playlists friends can edit: who sees what,
///     who changes what, and whose name ends up on a track.
/// </summary>
public class DomFriendsTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("dom-friends").FullName;

    public void Dispose() => Directory.Delete(_directory, true);

    [Fact]
    public void OneCodeAddsAWholeGroup()
    {
        var store = Store();
        var (kris, ana, boyan) = (User(store, "kris"), User(store, "ana"), User(store, "boyan"));
        var invite = store.OpenInvite(kris)!;

        Assert.Null(store.AcceptInvite(ana, invite.Code).error);
        Assert.Null(store.AcceptInvite(boyan, invite.Code).error);

        Assert.Equal(["ana", "boyan"], store.Friends(kris));
        Assert.Equal(["kris"], store.Friends(ana));
        Assert.Equal(["kris"], store.Friends(boyan)); // friends of kris, not of each other
        Assert.Equal(["ana", "boyan"], store.CurrentInvite(kris)!.Joined);
    }

    [Fact]
    public void ShowingTheCodeAgainGivesTheSameOneUntilItEnds()
    {
        var store = Store();
        var kris = User(store, "kris");

        var first = store.OpenInvite(kris)!;
        Assert.Equal(first.Code, store.OpenInvite(kris)!.Code);

        store.EndInvite(kris);
        var second = store.OpenInvite(kris)!;
        Assert.NotEqual(first.Code, second.Code);

        second.ExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(-1);
        Assert.Null(store.CurrentInvite(kris));
        Assert.NotEqual(second.Code, store.OpenInvite(kris)!.Code);
    }

    [Fact]
    public void ExpiredEndedAndOwnCodesAreRefused()
    {
        var store = Store();
        var (kris, ana) = (User(store, "kris"), User(store, "ana"));

        var invite = store.OpenInvite(kris)!;
        Assert.Equal("own_code", store.AcceptInvite(kris, invite.Code).error);

        invite.ExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(-1);
        Assert.Equal("invalid_code", store.AcceptInvite(ana, invite.Code).error);

        var ended = store.OpenInvite(kris)!;
        store.EndInvite(kris);
        Assert.Equal("invalid_code", store.AcceptInvite(ana, ended.Code).error);
        Assert.Null(store.PeekInvite(ended.Code));

        Assert.Empty(store.Friends(kris));
    }

    [Fact]
    public void ACodeIsForgivingAboutHowItWasTyped()
    {
        var store = Store();
        var (kris, ana) = (User(store, "kris"), User(store, "ana"));
        var code = store.OpenInvite(kris)!.Code;

        // lower case, a dash in the middle, and a letter O wherever the code has a zero
        var typed = (code[..4] + "-" + code[4..]).ToLowerInvariant().Replace('0', 'o');

        Assert.Equal("kris", store.PeekInvite(typed)?.username);
        Assert.Null(store.AcceptInvite(ana, typed).error);
    }

    [Fact]
    public void AcceptingTwiceChangesNothing()
    {
        var store = Store();
        var (kris, ana) = (User(store, "kris"), User(store, "ana"));
        var invite = store.OpenInvite(kris)!;

        store.AcceptInvite(ana, invite.Code);
        var (_, alreadyFriends, error, _) = store.AcceptInvite(ana, invite.Code);

        Assert.Null(error);
        Assert.True(alreadyFriends);
        Assert.Equal(["ana"], store.CurrentInvite(kris)!.Joined);
        Assert.Equal(["ana"], store.Friends(kris));
    }

    [Fact]
    public void AFriendsPlaylistIsForFriendsOnly()
    {
        var store = Store();
        var (kris, ana, stranger) = (User(store, "kris"), User(store, "ana"), User(store, "stranger"));
        Befriend(store, kris, ana);

        var friendsOnly = store.Create(kris, "Late shift", Visibility.Friends, []).playlist!;
        var hidden = store.Create(kris, "Diary", Visibility.Private, []).playlist!;

        Assert.NotNull(store.Visible(friendsOnly.Id, ana));
        Assert.Null(store.Visible(friendsOnly.Id, stranger));
        Assert.Null(store.Visible(friendsOnly.Id, null));
        Assert.Null(store.Visible(hidden.Id, ana));
        Assert.Equal([friendsOnly.Id], store.FriendsPlaylists(ana).Select(p => p.Id));
        Assert.Empty(store.FriendsPlaylists(stranger));
        Assert.Empty(store.Public());
    }

    [Fact]
    public void ACollaboratorChangesTheTracksAndNothingElse()
    {
        var store = Store();
        var (kris, ana, stranger) = (User(store, "kris"), User(store, "ana"), User(store, "stranger"));
        Befriend(store, kris, ana);
        var playlist = store.Create(kris, "Late shift", Visibility.Private, [Track("a")]).playlist!;
        store.Update(kris, playlist.Id, null, null, null, ["ANA"]);

        // a private playlist is still open to whoever can edit it, and in their Mine
        Assert.NotNull(store.Visible(playlist.Id, ana));
        Assert.Contains(store.Mine(ana), p => p.Id == playlist.Id);
        Assert.Equal(["ana"], playlist.Collaborators); // the friend's own spelling

        // listed once, under what ana edits, even when friends can see it anyway
        store.Update(kris, playlist.Id, null, Visibility.Friends, null);
        Assert.Empty(store.FriendsPlaylists(ana));

        Assert.True(store.Append(ana, playlist.Id, Track("b")).added);
        Assert.Null(store.Update(ana, playlist.Id, null, null, [Track("b"), Track("a")], revision: playlist.Revision).error);

        Assert.Equal("forbidden", store.Update(ana, playlist.Id, "Mine now", null, null).error);
        Assert.Equal("forbidden", store.Update(ana, playlist.Id, null, Visibility.Public, null).error);
        Assert.Equal("forbidden", store.Update(ana, playlist.Id, null, null, null, []).error);
        Assert.False(store.Delete(ana, playlist.Id).deleted);
        Assert.Equal("not_found", store.Append(stranger, playlist.Id, Track("c")).error);
    }

    [Fact]
    public void EachTrackKeepsWhoAddedIt()
    {
        var store = Store();
        var (kris, ana) = (User(store, "kris"), User(store, "ana"));
        Befriend(store, kris, ana);
        var playlist = store.Create(kris, "Late shift", Visibility.Friends, [Track("a")]).playlist!;
        store.Update(kris, playlist.Id, null, null, null, ["ana"]);

        store.Append(kris, playlist.Id, Track("b"));
        store.Append(ana, playlist.Id, Track("c"));

        // a reorder by ana, with a claim of her own that the server ignores, plus one new track
        var forged = Track("a");
        forged.AddedBy = "ana";
        store.Update(ana, playlist.Id, null, null, [Track("c"), forged, Track("b"), Track("d")], revision: playlist.Revision);

        Assert.Equal(["c", "a", "b", "d"], playlist.Tracks.Select(t => t.Id));
        Assert.Equal(["ana", null, null, "ana"], playlist.Tracks.Select(t => t.AddedBy));
    }

    [Fact]
    public void AStaleListIsRefusedRatherThanOverwritten()
    {
        var store = Store();
        var (kris, ana) = (User(store, "kris"), User(store, "ana"));
        Befriend(store, kris, ana);
        var playlist = store.Create(kris, "Late shift", Visibility.Private, [Track("a")]).playlist!;
        store.Update(kris, playlist.Id, null, null, null, ["ana"]);
        var seen = playlist.Revision;

        store.Append(ana, playlist.Id, Track("b")); // lands while kris's page still shows the old list

        var (_, error, _) = store.Update(kris, playlist.Id, null, null, [Track("a")], revision: seen);
        var (_, withoutRevision, _) = store.Update(kris, playlist.Id, null, null, [Track("a")]);

        Assert.Equal("stale", error);
        Assert.Equal("stale", withoutRevision);
        Assert.Equal(["a", "b"], playlist.Tracks.Select(t => t.Id));
    }

    [Fact]
    public void OnlyFriendsCanBeCollaborators()
    {
        var store = Store();
        var kris = User(store, "kris");
        User(store, "stranger");
        var playlist = store.Create(kris, "Late shift", Visibility.Private, []).playlist!;

        Assert.Equal("not_a_friend", store.Update(kris, playlist.Id, null, null, null, ["stranger"]).error);
        Assert.Empty(playlist.Collaborators);
    }

    [Fact]
    public void UnfriendingEndsTheEditingButKeepsTheTracks()
    {
        var store = Store();
        var (kris, ana) = (User(store, "kris"), User(store, "ana"));
        Befriend(store, kris, ana);
        var his = store.Create(kris, "Late shift", Visibility.Friends, []).playlist!;
        var hers = store.Create(ana, "Sofia nights", Visibility.Friends, []).playlist!;
        store.Update(kris, his.Id, null, null, null, ["ana"]);
        store.Update(ana, hers.Id, null, null, null, ["kris"]);
        store.Append(ana, his.Id, Track("a"));

        Assert.True(store.Unfriend(kris, "Ana"));

        Assert.Empty(store.Friends(kris));
        Assert.Empty(store.Friends(ana));
        Assert.Empty(his.Collaborators);
        Assert.Empty(hers.Collaborators);
        Assert.Null(store.Visible(his.Id, ana));
        Assert.Equal("ana", his.Tracks.Single().AddedBy);
    }

    [Fact]
    public void ARenameFollowsTheNameEverywhere()
    {
        var store = Store();
        var (kris, ana) = (User(store, "kris"), User(store, "ana"));
        Befriend(store, kris, ana);
        var playlist = store.Create(kris, "Late shift", Visibility.Friends, []).playlist!;
        store.Update(kris, playlist.Id, null, null, null, ["ana"]);
        store.Append(ana, playlist.Id, Track("a"));

        Assert.Null(store.Rename(ana, "correct horse battery", "anka").error);

        Assert.Equal(["anka"], store.Friends(kris));
        Assert.Equal(["anka"], playlist.Collaborators);
        Assert.Equal("anka", playlist.Tracks.Single().AddedBy);
        Assert.NotNull(store.Visible(playlist.Id, ana));
    }

    [Fact]
    public void DeletingAnAccountTakesItOffEveryListButKeepsItsTracks()
    {
        var store = Store();
        var (kris, ana) = (User(store, "kris"), User(store, "ana"));
        Befriend(store, kris, ana);
        var playlist = store.Create(kris, "Late shift", Visibility.Friends, []).playlist!;
        store.Update(kris, playlist.Id, null, null, null, ["ana"]);
        store.Append(ana, playlist.Id, Track("a"));
        store.OpenInvite(ana);

        store.DeleteAccount(ana, "correct horse battery");

        Assert.Empty(store.Friends(kris));
        Assert.Empty(playlist.Collaborators);
        Assert.Equal("ana", playlist.Tracks.Single().AddedBy);
    }

    [Fact]
    public void FriendsAndSharingSurviveARestart()
    {
        var store = Store();
        var (kris, ana) = (User(store, "kris"), User(store, "ana"));
        Befriend(store, kris, ana);
        var playlist = store.Create(kris, "Late shift", Visibility.Friends, []).playlist!;
        store.Update(kris, playlist.Id, null, null, null, ["ana"]);
        store.Append(ana, playlist.Id, Track("a"));

        var reopened = Store();
        var again = reopened.Login("ana", "correct horse battery").user!;
        var saved = reopened.Visible(playlist.Id, again)!;

        Assert.Equal(["kris"], reopened.Friends(again));
        Assert.Equal(Visibility.Friends, saved.Visibility);
        Assert.Equal(["ana"], saved.Collaborators);
        Assert.Equal("ana", saved.Tracks.Single().AddedBy);
        Assert.Equal(1, saved.Revision);
    }

    [Fact]
    public void AVersionOneFileKeepsItsPublicPlaylists()
    {
        var path = Path.Combine(_directory, "dom.json");
        File.WriteAllText(path, """
            {
              "Version": 1,
              "Users": [],
              "Playlists": [
                { "Id": "p_1", "Owner": "kris", "Name": "Out", "IsPublic": true, "Tracks": [] },
                { "Id": "p_2", "Owner": "kris", "Name": "In", "IsPublic": false, "Tracks": [] }
              ]
            }
            """);

        var store = Store();
        Assert.Equal(["p_1"], store.Public().Select(p => p.Id));

        // imported once and never written back: the file stays the way an older image reads it
        User(store, "radost");
        Assert.Contains("IsPublic", File.ReadAllText(path));
        Assert.Equal(["p_1"], Store().Public().Select(p => p.Id));
    }

    private static void Befriend(DomStore store, User inviter, User friend) =>
        Assert.Null(store.AcceptInvite(friend, store.OpenInvite(inviter)!.Code).error);

    private static User User(DomStore store, string username)
    {
        var (token, _, _, _) = store.Register(username, "correct horse battery");

        return store.Resolve(token!.Value)!;
    }

    private static TrackSnapshot Track(string id) => new() { Id = id, Name = "Nightcall", Artist = "Kavinsky" };

    private DomStore Store() => new(Path.Combine(_directory, "dom.json"), Logger.None);
}
