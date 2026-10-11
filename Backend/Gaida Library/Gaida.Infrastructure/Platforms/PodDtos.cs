using Gaida.Core;
using JetBrains.Annotations;

namespace Gaida.Infrastructure.Platforms;

/// <summary>A search/resolve/playlist/random result as a pod hands it over — no <c>contentUrl</c>, no public host.</summary>
public sealed record PodResultDto
{
    public string? Id { get; init; }
    public string? Name { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public string? Duration { get; init; }
    public string? ThumbnailUrl { get; init; }
    public string? OriginalTitle { get; init; }
    public string? OriginalArtist { get; init; }
}

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record PodBrowseFolderDto
{
    public string? Name { get; set; }
    public int Songs { get; set; }
}

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record PodBrowseDto
{
    public List<PodBrowseFolderDto>? Folders { get; set; }
    public List<PodResultDto>? Files { get; set; }
}

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record PodVariantDto
{
    public string? Match { get; set; }
    public double Score { get; set; }
    public int DurationDeltaSeconds { get; set; }
    public List<string>? YouTubeTags { get; set; }
    public List<string>? LibraryTags { get; set; }
    public PodResultDto? Result { get; set; }
}

internal sealed record ClassifyDto
{
    public string? Kind { get; init; }
    public string? Id { get; init; }
    public string? Error { get; init; }
}

/// <summary>
///     One pod's answer to <c>/classify</c>: what it thinks the query is, or its own error for a claimed-but-invalid
///     one.
/// </summary>
public readonly record struct ClassifyClaim(QueryType Kind, string Query, string? Error);
