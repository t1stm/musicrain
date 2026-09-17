using System.Text.Json.Serialization;

namespace Dom.Store;

/// <summary>
///     A named, ordered list of tracks belonging to one account. The tracks are snapshots, not
///     references: a playlist renders without asking Gaida anything.
/// </summary>
public sealed class Playlist
{
    public required string Id { get; init; }

    /// <summary>The owner's username as they typed it. <see cref="OwnerKey" /> is what ownership is decided on.</summary>
    public required string Owner { get; set; }

    public required string Name { get; set; }
    public bool IsPublic { get; set; }

    /// <summary>File name under <c>Dom:CoverDir</c>, or <c>null</c> when nobody uploaded one.</summary>
    public string? CoverFile { get; set; }

    public List<TrackSnapshot> Tracks { get; set; } = [];
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset UpdatedUtc { get; set; }

    [JsonIgnore] public string OwnerKey => User.Normalize(Owner);

    /// <summary>Everything in it, added up. The card and the hero both state this.</summary>
    [JsonIgnore]
    public TimeSpan Duration => Tracks.Aggregate(TimeSpan.Zero,
        (total, track) => total + (TimeSpan.TryParse(track.Duration, out var length) ? length : TimeSpan.Zero));
}

/// <summary>
///     A track as it looked when it was saved. Field-for-field the subset of the frontend's
///     <c>SearchResult</c> that a row needs, which is why the mapping on that side is one function.
/// </summary>
public sealed class TrackSnapshot
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Artist { get; init; }
    public string? Album { get; init; }

    /// <summary>A <see cref="TimeSpan" /> string, <c>hh:mm:ss</c> — the shape the rest of the API speaks.</summary>
    public string Duration { get; init; } = "00:00:00";

    public string? ThumbnailUrl { get; init; }
}
