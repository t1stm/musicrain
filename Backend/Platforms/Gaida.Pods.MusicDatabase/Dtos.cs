using JetBrains.Annotations;

namespace Gaida.Pods.MusicDatabase;

// The public shape of this pod. Deliberately not shared with Gaida.API/Contracts/DiscoveryContracts.cs: this pod
// doesn't know the public host (no contentUrl), and Gaida.API adds fields (contentUrl) this must not.

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record ResultDto(
    string Id,
    string? Name,
    string? Artist,
    string? Album,
    string Duration,
    string? ThumbnailUrl,
    string? OriginalTitle,
    string? OriginalArtist,
    // Only /resolve fills these, and only stih reads them — see LYRICS_1_LIBRARY_PLAN.md.
    string? RelativeLocation = null,
    string? LyricsType = null,
    string? LyricsSource = null,
    // Every name the library finds the track by, so the bot's -p can rank it the way the search matched it.
    IReadOnlyList<string>? TitleVariants = null,
    IReadOnlyList<string>? ArtistVariants = null);

/// <summary>One track stih's sweep has not found words for yet.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record MissingLyricsDto(
    string Id,
    string? Title,
    string? Artist,
    string? Album,
    string Duration,
    string? RelativeLocation);

/// <summary>What one track's lyrics state became, after a stamp.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record LyricsRowDto(string Id, string? Type, string? Source, string? Checked);

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record LibraryRowDto(
    string Id,
    IReadOnlyList<string> Titles,
    IReadOnlyList<string> Artists,
    string? Album,
    string? Location,
    string Duration,
    string? CoverUrl);

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record ClassifyDto(string? Kind, string? Id, string? Error);

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record ErrorDto(string Error);

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record BrowseFolderDto(string Name, string Path, int Songs);

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record BrowseDto(string Path, IReadOnlyList<BrowseFolderDto> Folders, IReadOnlyList<ResultDto> Files);

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record VariantDto(
    string Match,
    double Score,
    int DurationDeltaSeconds,
    IReadOnlyList<string> YouTubeTags,
    IReadOnlyList<string> LibraryTags,
    ResultDto Result);
