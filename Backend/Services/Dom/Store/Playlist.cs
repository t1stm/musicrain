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
    public Visibility Visibility { get; set; }

    /// <summary>
    ///     Friends of the owner who may change the tracks and nothing else. Display names, compared
    ///     through <see cref="User.Normalize" />, rewritten on rename like <see cref="Owner" />.
    ///     Replaced, never mutated: a response may be walking the old list outside the lock.
    /// </summary>
    public List<string> Collaborators { get; set; } = [];

    /// <summary>
    ///     Bumped on every change to the tracks. A save that replaces the list names the revision it
    ///     started from, so a stale page cannot silently drop what a collaborator added meanwhile.
    /// </summary>
    public int Revision { get; set; }

    /// <summary>Version 1 files only: read on load, folded into <see cref="Visibility" />, never written again.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsPublic { get; set; }

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

    /// <summary>
    ///     Who put it here: a collaborator's display name, or <c>null</c> for the owner. Decided by the
    ///     server from who saved it — whatever the client sends here is dropped by <c>Clean</c>.
    /// </summary>
    public string? AddedBy { get; set; }
}

/// <summary>Who can open a playlist. Stored and sent as the lower-case name.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Visibility>))]
public enum Visibility
{
    [JsonStringEnumMemberName("private")] Private,
    [JsonStringEnumMemberName("friends")] Friends,
    [JsonStringEnumMemberName("public")] Public
}
