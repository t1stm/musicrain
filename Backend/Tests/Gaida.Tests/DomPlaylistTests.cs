using Dom.Store;
using Serilog.Core;

namespace Gaida.Tests;

/// <summary>Adding one track from a menu: at the end, once, and only to your own playlist.</summary>
public class DomPlaylistTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("dom-playlists").FullName;

    public void Dispose() => Directory.Delete(_directory, true);

    [Fact]
    public void AppendPutsATrackAtTheEndOnce()
    {
        var store = Store();
        var owner = User(store, "radost");
        var (playlist, _, _) = store.Create(owner, "Late shift", false, [Track("audio://a")]);

        var (_, added, error, _) = store.Append(owner, playlist!.Id, Track("yt://b"));
        var (again, addedAgain, _, _) = store.Append(owner, playlist.Id, Track("yt://b"));

        Assert.Null(error);
        Assert.True(added);
        Assert.False(addedAgain);
        Assert.Equal(["audio://a", "yt://b"], again!.Tracks.Select(t => t.Id));
    }

    [Fact]
    public void AppendToSomebodyElsesPlaylistIsNotFound()
    {
        var store = Store();
        var (playlist, _, _) = store.Create(User(store, "radost"), "Late shift", true, []);

        var (_, added, error, _) = store.Append(User(store, "boyan"), playlist!.Id, Track("yt://b"));

        Assert.False(added);
        Assert.Equal("not_found", error);
        Assert.Empty(playlist.Tracks);
    }

    private static User User(DomStore store, string username)
    {
        var (token, _, _, _) = store.Register(username, "correct horse battery");

        return store.Resolve(token!.Value)!;
    }

    private static TrackSnapshot Track(string id) => new() { Id = id, Name = "Nightcall", Artist = "Kavinsky" };

    private DomStore Store() => new(Path.Combine(_directory, "dom.json"), Logger.None);
}
