using System.Globalization;
using Gaida.Core.Platforms;
using JetBrains.Annotations;

namespace Gaida.Pods.YouTube;

/// <summary>
///     The pod result shape -- no <c>contentUrl</c>, this platform doesn't know the public host. Gaida.API adds it
///     (see <c>DiscoveryContracts.cs</c>).
/// </summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record ResultDto(
    string Id,
    string Name,
    string Artist,
    string? Album,
    string Duration,
    string? ThumbnailUrl,
    string? OriginalTitle,
    string? OriginalArtist);

/// <summary><c>/classify</c> response body. On a 200, only Kind/Id are set; on a 400, only Error.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record ClassifyDto(string? Kind, string? Id, string? Error);

public static class ResultMapper
{
    /// <summary>Mirrors DiscoveryContracts.cs's duration clamp so both sides render the same value.</summary>
    public static ResultDto? Map(PlatformResult result)
    {
        if (string.IsNullOrWhiteSpace(result.Id)) return null;

        return new ResultDto(
            result.Id,
            string.IsNullOrWhiteSpace(result.Name) ? "Unknown title" : result.Name,
            string.IsNullOrWhiteSpace(result.Artist) ? "Unknown artist" : result.Artist,
            result.Album,
            result.Duration < TimeSpan.Zero
                ? TimeSpan.Zero.ToString("c", CultureInfo.InvariantCulture)
                : result.Duration.ToString("c", CultureInfo.InvariantCulture),
            result.ThumbnailUrl,
            result.OriginalTitle,
            result.OriginalArtist);
    }
}
