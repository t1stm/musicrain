using Gaida.Core;
using Gaida.Core.Platforms;
using ILogger = Serilog.ILogger;

namespace Gaida.API;

/// <summary>
///     Builds the platform list from config and hands out the <see cref="AudioManager" />. No caching, no
///     coalescing — Dunav sits upstream of every content request and owns both now.
/// </summary>
public class ManagerService
{
    public readonly AudioManager Manager;

    public ManagerService(ILogger logger, IConfiguration configuration, IHttpClientFactory httpClientFactory)
    {
        Manager = new AudioManager(logger);
        var metadataOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pod in configuration.GetSection("Platforms").GetChildren())
        {
            var url = pod["Url"];
            var ids = pod["Ids"]?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (string.IsNullOrWhiteSpace(url) || ids is not { Length: > 0 }) continue;

            var http = httpClientFactory.CreateClient($"platform-{pod.Key}");
            http.BaseAddress = new Uri(url);

            // A pod whose results are names rather than playable IDs (Spotify). One flag in config beats an
            // interface or a per-result marker on the wire: the pod itself has no opinion about it.
            if (pod.GetValue("Resolve", false)) metadataOnly.UnionWith(ids);

            Manager.RegisterPlatform(new HttpPlatform(logger, http, ids));
        }

        MetadataOnly = metadataOnly;
    }

    /// <summary>
    ///     Platform identifiers (<c>spotify://</c> and the like) whose results carry no audio and have to be
    ///     resolved against a platform that does — see <see cref="PlayableResolver" />.
    /// </summary>
    private IReadOnlySet<string> MetadataOnly { get; }

    /// <summary>Whether <paramref name="id" /> (or a canonical query) belongs to a metadata-only platform.</summary>
    public bool NeedsResolving(string? id)
    {
        return id is not null &&
               MetadataOnly.Any(identifier => id.StartsWith(identifier, StringComparison.OrdinalIgnoreCase));
    }
}
