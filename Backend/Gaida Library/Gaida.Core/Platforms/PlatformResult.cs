using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Gaida.Core.Platforms.Optional.Supports;
using Gaida.Core.Streams;
using Gaida.Core.Utils;

namespace Gaida.Core.Platforms;

[SuppressMessage("Microsoft.Design", "CA1051:DoNotDeclareVisibleInstanceFields",
    Justification = "YouTube.json's import deserializes results, and System.Text.Json skips a required field it " +
                    "ignores but refuses a required property it ignores. As fields, the import reads them as it " +
                    "always has.")]
public abstract class PlatformResult
{
    [JsonIgnore] public required IReadOnlyList<ContentGetter> Downloaders = [];
    [JsonInclude] public required string Id;
    [JsonInclude] public string? Name { get; set; }
    [JsonInclude] public string? Artist { get; set; }
    [JsonInclude] public string? Album { get; set; }
    [JsonInclude] public TimeSpan Duration { get; set; }
    [JsonInclude] public string? ThumbnailUrl { get; set; }

    /// <summary>Untransliterated title, when the platform has one.</summary>
    [JsonInclude]
    public string? OriginalTitle { get; set; }

    /// <summary>Untransliterated artist, when the platform has one.</summary>
    [JsonInclude]
    public string? OriginalArtist { get; set; }

    /// <summary>
    ///     Every title the library finds this track by — romanizations and alternate names included — so a
    ///     client ranking hits can match what the library matched. Only the library fills it.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string>? TitleVariants { get; set; }

    /// <summary>As <see cref="TitleVariants" />, for the artist.</summary>
    [JsonIgnore]
    public IReadOnlyList<string>? ArtistVariants { get; set; }

    public ReadOnlySpan<char> GetPureId()
    {
        return PlatformId.StripPrefix(Id);
    }

    /// <returns>The content stream, or <c>null</c> when no downloader could provide it.</returns>
    public async Task<StreamSpreader?> GetContentDataAsync(CancellationToken token = default)
    {
        foreach (var downloader in Downloaders)
        {
            var result = await downloader.GetContentDataAsync(this, token);
            if (result is null) continue;
            if (this is ISupportsCaching caching) caching.RunCacheProcess(result);
            return result;
        }

        return null;
    }
}
