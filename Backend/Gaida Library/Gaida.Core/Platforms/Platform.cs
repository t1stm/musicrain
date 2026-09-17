using Gaida.Core.Platforms.Optional.Supports;
using Serilog;

namespace Gaida.Core.Platforms;

public abstract class Platform : ISupportsId
{
    protected Platform(ILogger logger)
    {
        Logger = logger.ForContext(GetType());
    }

    protected ILogger Logger { get; }

    protected abstract HashSet<string> SearchIdIdentifiers { get; }

    public HashSet<string>.AlternateLookup<ReadOnlySpan<char>> SearchIdIdentifiersLookup =>
        SearchIdIdentifiers.GetAlternateLookup<ReadOnlySpan<char>>();

    protected abstract List<SearchProvider> SearchProviders { get; set; }
    protected abstract List<ContentGetter> ContentDownloaders { get; set; }

    public virtual async Task<PlatformResult?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        foreach (var searchProvider in SearchProviders.OfType<ISupportsId>())
            try
            {
                var result = await searchProvider.GetByIdAsync(id, cancellationToken);
                if (result is not null) return result;
            }
            catch (Exception e)
            {
                Logger.Error(e, "Provider {Provider} failed for ID {ID}", searchProvider.GetType().Name, id);
            }

        return null;
    }

    public void Initialize()
    {
        SearchProviders = [.. SearchProviders.OrderByDescending(x => x.Priority)];
        ContentDownloaders = [.. ContentDownloaders.OrderByDescending(x => x.Priority)];

        SearchProviders.ForEach(s => s.RegisterContentDownloaders(ContentDownloaders));
    }
}
