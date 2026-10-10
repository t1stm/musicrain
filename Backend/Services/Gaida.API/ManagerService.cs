using Gaida.Core;
using Gaida.Core.Platforms;
using Gaida.Core.Utils;
using Gaida.Infrastructure.Platforms;
using ILogger = Serilog.ILogger;

namespace Gaida.API;

/// <summary>
///     Builds the platform list from config and hands out the <see cref="AudioManager" />. No caching, no
///     coalescing — Dunav sits upstream of every content request and owns both now.
/// </summary>
public class ManagerService
{
    /// <summary>Every pod, in config order, which is the order <see cref="ClassifyAsync" /> asks them in.</summary>
    private readonly List<HttpPlatform> _pods = [];

    public ManagerService(ILogger logger, IConfiguration configuration, IHttpClientFactory httpClientFactory)
    {
        Manager = new AudioManager(logger);
        var metadataOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var section in configuration.GetSection("Platforms").GetChildren())
        {
            var url = section["Url"];
            var ids = section["Ids"]?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (string.IsNullOrWhiteSpace(url) || ids is not { Length: > 0 }) continue;

            var http = httpClientFactory.CreateClient($"platform-{section.Key}");
            http.BaseAddress = new Uri(url);

            // A pod whose results are names rather than playable IDs (Spotify). One flag in config beats an
            // interface or a per-result marker on the wire: the pod itself has no opinion about it.
            if (section.GetValue("Resolve", false)) metadataOnly.UnionWith(ids);

            var pod = new HttpPlatform(logger, http, ids);
            Manager.RegisterPlatform(pod);
            _pods.Add(pod);
        }

        MetadataOnly = metadataOnly;
    }

    public AudioManager Manager { get; }

    /// <summary>
    ///     Platform identifiers (<c>spotify://</c> and the like) whose results carry no audio and have to be
    ///     resolved against a platform that does — see <see cref="PlayableResolver" />.
    /// </summary>
    private IReadOnlySet<string> MetadataOnly { get; }

    /// <summary>The pod that owns <paramref name="prefix" /> (e.g. <c>"audio://"</c>), or <c>null</c> when none does.</summary>
    public HttpPlatform? Pod(string prefix)
    {
        return Manager.PlatformFor(prefix) as HttpPlatform;
    }

    /// <summary>Whether <paramref name="id" /> (or a canonical query) belongs to a metadata-only platform.</summary>
    public bool NeedsResolving(string? id)
    {
        return id is not null &&
               MetadataOnly.Any(identifier => id.StartsWith(identifier, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Fans <c>/classify</c> out across every pod. First claim wins; nobody claiming means an ordinary
    ///     keyword search, which is the one classification rule that stays in Gaida.
    /// </summary>
    public async Task<ClassifyClaim> ClassifyAsync(string query, CancellationToken cancellationToken = default)
    {
        var trimmed = query.Trim();
        foreach (var pod in _pods)
        {
            var claim = await pod.ClassifyAsync(trimmed, cancellationToken);
            if (claim is not null) return claim.Value;
        }

        return new ClassifyClaim(QueryType.Keywords, trimmed, null);
    }

    /// <summary>
    ///     <paramref name="count" /> results drawn from YouTube's cache and the library, YouTube's share being
    ///     <paramref name="youTubeShare" />. Randomized rounding preserves the share over time while letting
    ///     either source be picked for small requests (count=1 chooses YouTube 40% of the time at 0.4).
    /// </summary>
    public IAsyncEnumerable<PlatformResult> RandomResults(int count, double youTubeShare,
        CancellationToken cancellationToken)
    {
        var exactYouTubeCount = count * youTubeShare;
        var youTubeCount = (int)Math.Floor(exactYouTubeCount);
        if (Random.Shared.NextDouble() < exactYouTubeCount - youTubeCount)
            youTubeCount++;

        var youTube = Pod(Prefix.YouTube)?.GetRandomResults(youTubeCount, cancellationToken)
                      ?? Array.Empty<PlatformResult>().AsAsync();

        // ponytail: local is asked for the full count rather than the shortfall. It answers from an in-memory
        // shuffle, so over-asking is free, and whatever YouTube turns out to be short of is already on its
        // way — which is the only way a backfill can work when neither source has finished yet.
        var local = Pod(Prefix.Local)?.GetRandomResults(count, cancellationToken)
                    ?? Array.Empty<PlatformResult>().AsAsync();

        return youTube.RandomMerge(youTubeCount, local, count, cancellationToken);
    }
}
