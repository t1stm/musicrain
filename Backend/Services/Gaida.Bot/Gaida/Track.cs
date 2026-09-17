using System.Text.Json.Serialization;
using DSharpPlus.Entities;

namespace Gaida.Bot.Gaida;

/// <summary>One discovery result, exactly as the API sends it, plus what the bot displays.</summary>
public sealed record Track
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Artist { get; init; }
    public string? Album { get; init; }
    public required string ContentUrl { get; init; }
    public required string Duration { get; init; }
    public string? ThumbnailUrl { get; init; }
    public string? OriginalTitle { get; init; }
    public string? OriginalArtist { get; init; }

    /// <summary>Who queued it. Local to the bot; never on the wire.</summary>
    [JsonIgnore]
    public DiscordMember? Requester { get; set; }

    /// <summary>The old <c>PlayableItem.GetName()</c>.</summary>
    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Artist) ? Name : $"{Name} - {Artist}";

    /// <summary>The old <c>ILanguage.GetTypeOfTrack</c>, decided by the ID's platform prefix.</summary>
    [JsonIgnore]
    public string Kind => Id switch
    {
        _ when Id.StartsWith("audio://", StringComparison.Ordinal) => "Music",
        _ when Id.StartsWith("yt://", StringComparison.Ordinal) => "Youtube Video",
        _ when Id.StartsWith("deezer://", StringComparison.Ordinal) => "Deezer Track",
        _ => "Item"
    };

    /// <summary><see cref="TimeSpan.Zero" /> stands for the old "unknown length", which renders as ∞.</summary>
    [JsonIgnore]
    public TimeSpan Length => TimeSpan.TryParse(Duration, out var parsed) ? parsed : TimeSpan.Zero;
}

/// <summary>What <c>/Audio/FindQueryType</c> says a pasted value is.</summary>
public sealed record QueryResolution
{
    public required string Kind { get; init; }
    public required string Query { get; init; }
    public string? PlaylistId { get; init; }
    public Track? Result { get; init; }

    /// <summary>A playlist is the one kind whose every track belongs in the queue.</summary>
    public bool IsPlaylist => Kind is "youtubePlaylist" or "spotifyPlaylist" or "deezerPlaylist";
}

/// <summary>What <c>/Audio/Lyrics/Get</c> returns. The bot only ever prints <see cref="Text" />.</summary>
public sealed record LyricsResult
{
    public required string Type { get; init; }
    public string? Source { get; init; }
    public string? Text { get; init; }
}
