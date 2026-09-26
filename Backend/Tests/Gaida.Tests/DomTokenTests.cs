using Dom.Store;
using Serilog.Core;

namespace Gaida.Tests;

/// <summary>The token expiry slides on use, and the write that records it is throttled.</summary>
public class DomTokenTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("dom-tokens").FullName;

    public void Dispose() => Directory.Delete(_directory, true);

    [Fact]
    public void ResolveSlidesAnExpiryThatHasMovedByMoreThanTheGranularity()
    {
        var store = Store();
        var (token, _, _, _) = store.Register("radost", "correct horse battery");

        // two days spent: a fresh thirty is more than a day past what is on the token
        token!.ExpiresUtc = DateTimeOffset.UtcNow.AddDays(28);

        Assert.NotNull(store.Resolve(token.Value));
        Assert.True(store.ExpiryOf(token.Value) > DateTimeOffset.UtcNow.AddDays(29.9));
    }

    [Fact]
    public void ResolveLeavesAnExpiryThatHasBarelyMoved()
    {
        var store = Store();
        var (token, _, _, _) = store.Register("radost", "correct horse battery");
        var issued = store.ExpiryOf(token!.Value);

        // an hour later a fresh thirty is an hour past the stored one, which is not worth a write
        Assert.NotNull(store.Resolve(token.Value));
        Assert.Equal(issued, store.ExpiryOf(token.Value));
    }

    [Fact]
    public void ResolveStillRefusesAnExpiredToken()
    {
        var store = Store();
        var (token, _, _, _) = store.Register("radost", "correct horse battery");
        token!.ExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(-1);

        Assert.Null(store.Resolve(token.Value));
        Assert.Null(store.ExpiryOf(token.Value));
    }

    private DomStore Store() => new(Path.Combine(_directory, "dom.json"), Logger.None);
}
