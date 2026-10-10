using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Gaida.Core.Platforms;
using Gaida.Core.Platforms.Optional.Supports;
using Gaida.Core.Utils;

namespace Gaida.Tests;

public class PlatformTests
{
    [Fact]
    public async Task AProviderThatFailsFallsThroughToTheNext()
    {
        var platform = new TestPlatform(new Provider(10, _ => throw new InvalidOperationException("down")),
            new Provider(5, id => new Result(id)));

        Assert.Equal("found", (await platform.GetByIdAsync("found"))?.Id);
    }

    [Fact]
    public async Task ACancelledLookupIsNotSwallowedAsAProviderFailure()
    {
        var platform = new TestPlatform(new Provider(10, _ => throw new OperationCanceledException()),
            new Provider(5, id => new Result(id)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => platform.GetByIdAsync("x"));
    }

    private sealed class TestPlatform : Platform
    {
        public TestPlatform(params SearchProvider[] providers) : base(Serilog.Core.Logger.None)
        {
            SearchProviders = [.. providers];
            ContentDownloaders = [];
            Initialize();
        }

        protected override HashSet<string> SearchIdIdentifiers { get; } = ["test://"];
    }

    private sealed class Provider(int priority, Func<string, PlatformResult> find) : SearchProvider(Serilog.Core.Logger.None),
        ISupportsId
    {
        public override int Priority => priority;

        public Task<PlatformResult?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<PlatformResult?>(find(id));
        }
    }

    private sealed class Result : PlatformResult
    {
        [SetsRequiredMembers]
        public Result(string id)
        {
            Id = id;
            Downloaders = [];
        }
    }
}

/// <summary>The matching helpers fold case the same way whatever culture the host runs under.</summary>
public class CultureTests
{
    [Fact]
    public void FormattingIsTheSameUnderATurkishCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("tr-TR");

        try
        {
            Assert.Equal("title", LevenshteinDistance.RemoveFormatting("TITLE"));
            Assert.Equal("IVAN", Romanize.FromCyrillic("ИВАН"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
