using System.Text.Json.Serialization;
using JetBrains.Annotations;

namespace Stih;

// ── The public answer ───────────────────────────────────────────────────────────────────────────

/// <summary>One line of a song. <c>At</c> is <c>null</c> for unsynchronized lyrics, where there is no clock.</summary>
public sealed record LyricLineDto(double? At, string Text);

/// <summary>What the words actually belong to, which is not always what was asked for.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record MatchedDto(string Title, string? Artist, int Length);

/// <summary>
///     The body of <c>GET /Audio/Lyrics/Get</c>. <c>Lines</c> is present for both types so a client renders
///     one component either way and only the highlighting branches.
/// </summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record LyricsDto(
    string Type,
    string? Source,
    IReadOnlyList<LyricLineDto> Lines,
    string Text,
    MatchedDto? Matched);

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record ApiError(string Code, string Message);

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record ApiErrorBody(ApiError Error);

// ── The index on disk ───────────────────────────────────────────────────────────────────────────

/// <summary>Which shape of lyrics a row or a file holds. Serialized by name.</summary>
public enum LyricsKind
{
    Unsynchronized,
    Synchronized
}

/// <summary>Who found the words. <c>null</c> on a row means the file was already in the folder.</summary>
public enum LyricsOrigin
{
    Deezer,

    [JsonStringEnumMemberName("LRCLIB")] Lrclib
}

/// <summary>Which of the two mounts a row's path is relative to, so moving either does not invalidate it.</summary>
public enum LyricsVolume
{
    Library,
    Own
}

/// <summary>
///     One row of <c>Lyrics.json</c>: one track stih has looked at, hit or miss.
/// </summary>
/// <param name="Type"><c>null</c> for "looked, found nothing".</param>
/// <param name="Source"><c>null</c> for a file that was already in the folder.</param>
/// <param name="Checked">
///     When the lookup happened. A <c>null</c> <paramref name="Type" /> older than
///     <c>LYRICS_RETRY_DAYS</c> is retried, because LRCLIB grows; a hit is never re-fetched.
/// </param>
public sealed record LyricsRow(
    string Id,
    LyricsKind? Type,
    LyricsOrigin? Source,
    LyricsVolume? Volume,
    string? Path,
    DateTimeOffset Checked);

// ── The wire, inbound ───────────────────────────────────────────────────────────────────────────

/// <summary>What a platform pod pushes to <c>POST /register</c> after it downloads a track.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record RegisterDto(string? Id, string? Source, string? Text, string? Lrc);

/// <summary>A pod's <c>/resolve</c> answer. The last three are gaida-local's alone; the others ignore them.</summary>
public sealed record PodResultDto(
    string? Id,
    string? Name,
    string? Artist,
    string? Album,
    string? Duration,
    string? ThumbnailUrl,
    string? OriginalTitle,
    string? OriginalArtist,
    string? RelativeLocation,
    string? LyricsType,
    string? LyricsSource);

/// <summary>One row of gaida-local's <c>/lyrics/missing</c>: everything the sweep needs, with no second call.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record MissingLyricsDto(
    string Id,
    string? Title,
    string? Artist,
    string? Album,
    string? Duration,
    string? RelativeLocation);

/// <summary>
///     What a track is, however it was learned — from a pod's <c>/resolve</c> or from a sweep page.
/// </summary>
/// <param name="RelativeLocation">
///     Where the audio lives inside the library, for an <c>audio://</c> track. It comes from the pod that
///     owns the tree rather than being guessed from the title, so a file with an unusual name is still
///     matched exactly.
/// </param>
public sealed record Track(
    string Id,
    string Title,
    string? Artist,
    string? Album,
    TimeSpan Duration,
    string? RelativeLocation,
    LyricsKind? KnownType);

/// <summary>One LRCLIB candidate, exactly as their API shapes it.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record LrcLibResult(
    int Id,
    string? TrackName,
    string? ArtistName,
    string? AlbumName,
    double? Duration,
    bool Instrumental,
    string? PlainLyrics,
    string? SyncedLyrics);
