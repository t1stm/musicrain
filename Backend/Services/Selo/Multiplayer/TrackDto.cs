using JetBrains.Annotations;

namespace Selo.Multiplayer;

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class TrackDto
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public TimeSpan Duration { get; init; }
    public string? ThumbnailUrl { get; init; }
    public string? OriginalTitle { get; init; }
    public string? OriginalArtist { get; init; }
}
