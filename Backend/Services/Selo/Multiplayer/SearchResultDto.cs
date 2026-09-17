using System.Globalization;
using JetBrains.Annotations;

namespace Selo.Multiplayer;

/// <summary>
///     Mirrors <c>Gaida.API.Contracts.SearchResultDto</c> — the shape <c>GET /Audio/Search</c>
///     returns. Duplicated rather than referenced because Selo takes no project reference into
///     this repo; keep the two in sync if that contract ever changes.
/// </summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record SearchResultDto(
    string Id,
    string? Name,
    string? Artist,
    string? Album,
    string ContentUrl,
    string Duration,
    string? ThumbnailUrl,
    string? OriginalTitle,
    string? OriginalArtist)
{
    public TrackDto ToTrack()
    {
        return new TrackDto
        {
            Id = Id,
            Name = Name,
            Artist = Artist,
            Album = Album,
            Duration = TimeSpan.TryParse(Duration, CultureInfo.InvariantCulture, out var duration)
                ? duration
                : TimeSpan.Zero,
            ThumbnailUrl = ThumbnailUrl,
            OriginalTitle = OriginalTitle,
            OriginalArtist = OriginalArtist
        };
    }
}
