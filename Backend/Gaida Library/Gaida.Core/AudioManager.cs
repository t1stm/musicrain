using System.Runtime.CompilerServices;
using Gaida.Core.Platforms;
using Gaida.Core.Platforms.Optional.Supports;
using Gaida.Core.Utils;
using Serilog;

namespace Gaida.Core;

public class AudioManager(ILogger logger)
{
    private readonly Dictionary<string, Platform> _searchIdMap = [];

    private ILogger Logger { get; } = logger.ForContext<AudioManager>();

    private List<Platform> Platforms { get; } = [];

    private Dictionary<string, Platform>.AlternateLookup<ReadOnlySpan<char>> SearchIdLookup =>
        _searchIdMap.GetAlternateLookup<ReadOnlySpan<char>>();

    public void RegisterPlatform(Platform platform)
    {
        platform.Initialize();
        Platforms.Add(platform);

        foreach (var identifier in platform.SearchIdIdentifiersLookup.Set) SearchIdLookup.TryAdd(identifier, platform);
    }

    /// <summary>The registered platform that owns <paramref name="identifier" /> (e.g. <c>"audio://"</c>), if any.</summary>
    public Platform? PlatformFor(string identifier)
    {
        return SearchIdLookup.TryGetValue(identifier.AsSpan(), out var platform) ? platform : null;
    }

    /// <returns>The result, or <c>null</c> when no platform claims the ID or the lookup fails.</returns>
    public Task<PlatformResult?> SearchId(string id, CancellationToken cancellationToken = default)
    {
        Logger.Information("Searching for ID: {ID}", id);
        var normalizedId = id.Trim();
        var separator = normalizedId.IndexOf("://", StringComparison.Ordinal);
        if (separator < 1)
        {
            Logger.Warning("ID does not contain a platform protocol: {ID}", normalizedId);
            return Task.FromResult<PlatformResult?>(null);
        }

        var identifier = normalizedId[..(separator + 3)];
        if (SearchIdLookup.TryGetValue(identifier.AsSpan(), out var platform))
            return platform.GetByIdAsync(normalizedId[(separator + 3)..], cancellationToken);

        Logger.Warning("No platform found for identifier: {Identifier}", identifier);
        return Task.FromResult<PlatformResult?>(null);
    }

    /// <summary>
    ///     Asks every searchable platform at once and yields hits as they land, so the results arrive mixed
    ///     rather than one platform's block after another's, and the search costs the slowest pod instead of
    ///     all of them added up. Ordering is the consumer's job.
    /// </summary>
    public async IAsyncEnumerable<PlatformResult> SearchKeywords(string query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Logger.Information("Searching for keywords: {Query}", query);
        var totalResults = 0;

        await foreach (var result in Platforms.OfType<ISupportsSearch>()
                           .Select(platform => platform.SearchKeywords(query, cancellationToken)
                               .Guarded(Logger, platform.GetType().Name, cancellationToken))
                           .Merge(cancellationToken))
        {
            totalResults++;
            yield return result;
        }

        Logger.Debug("Keyword search for {Query} finished with {Count} results", query, totalResults);
    }

    public async IAsyncEnumerable<PlatformResult> SearchPlaylist(string query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Logger.Information("Searching for playlist: {Query}", query);
        var totalResults = 0;

        await foreach (var result in Platforms.OfType<ISupportsPlaylist>()
                           .Select(platform => platform.SearchPlaylist(query, cancellationToken)
                               .Guarded(Logger, platform.GetType().Name, cancellationToken))
                           .Merge(cancellationToken))
        {
            totalResults++;
            yield return result;
        }

        Logger.Debug("Playlist search for {Query} finished with {Count} results", query, totalResults);
    }

    /// <summary>
    ///     Fans <c>/classify</c> out across every HTTP platform pod. First claim wins; nobody claiming
    ///     means an ordinary keyword search, which is the one classification rule that stays in Gaida.
    /// </summary>
    public async Task<ClassifyClaim> ClassifyAsync(string query, CancellationToken cancellationToken = default)
    {
        var trimmed = query.Trim();
        foreach (var platform in Platforms.OfType<HttpPlatform>())
        {
            var claim = await platform.ClassifyAsync(trimmed, cancellationToken);
            if (claim is not null) return claim.Value;
        }

        return new ClassifyClaim(QueryType.Keywords, trimmed, null);
    }
}
