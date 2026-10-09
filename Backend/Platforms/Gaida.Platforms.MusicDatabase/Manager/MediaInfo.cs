using System.Text;
using ATL;
using Gaida.Core.Utils;

namespace Gaida.Platforms.MusicDatabase.Manager;

/// <summary>
///     Every tag and picture the library reads, through <see href="https://github.com/Zeugma440/atldotnet">ATL</see>:
///     in-process, about a millisecond a file against the 50 ms an ffprobe cost, and one reader for every format
///     where there used to be ffprobe, metaflac and TagLib# side by side.
/// </summary>
public static class MediaInfo
{
    /// <summary>What repeated tag values are joined with: the separator matching already splits on.</summary>
    private const string ArtistSeparator = ", ";

    static MediaInfo()
    {
        // A file with no title tag takes its names from the path in AddNames, which knows the library's
        // Author - Title layout. ATL's own fallback would put the whole filename in front of them as the title.
        Settings.UseFileNameWhenNoTitle = false;

        // ATL catches what it cannot parse and returns what it did read. Its stack trace on stdout is noise.
        Settings.OutputStacktracesToConsole = false;

        // Windows-1251, for Recovered. .NET ships only the Unicode encodings and Latin-1 without it.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>One file, with the settings above applied. Pictures are read only when asked for.</summary>
    public static Track Read(string location)
    {
        return new Track(location);
    }

    public static MusicInfo GetInformation(string location)
    {
        var track = Read(location);
        var title = Tag(track.Title);
        var artists = track.AdditionalFields
            .FirstOrDefault(field => field.Key.Equals("ARTISTS", StringComparison.OrdinalIgnoreCase)).Value;

        return new MusicInfo
        {
            Id = string.Empty,
            Length = track.DurationMs,
            Titles = MusicInfo.Variants(title),
            TitleRecovered = title is not null && title != track.Title,
            // ARTIST first: it is the credit as released. ARTISTS is a tagger's list, and in this library as often
            // the romanized names (Kondio for Кондьо) or only the featured act as the whole of it.
            Artists = MusicInfo.Variants(Merge(Tag(track.Artist)), Merge(Tag(artists))),
            Album = Tag(track.Album)?.Trim() is { Length: > 0 } album ? album : null
        };
    }

    /// <summary>
    ///     A file may carry the same tag more than once — a FLAC with an ARTISTS comment per performer is the
    ///     common case — and ATL hands those back as one string joined with ";". Rejoined on the library
    ///     separator so every name survives and <see cref="TitleNormalizer.SplitArtists" /> can split them.
    /// </summary>
    public static string? Merge(string? value)
    {
        return value?.Contains(';') != true
            ? value
            : string.Join(ArtistSeparator,
                value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>A tag worth reading, cp1251 recovered, or <c>null</c> for an absent or unrecoverable one.</summary>
    private static string? Tag(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (!Garbled(value)) return value;

        return Recovered(value) is { } recovered && !Garbled(recovered) ? recovered : null;
    }

    /// <summary>
    ///     A cp1251 tag that was read as Latin-1, decoded as what it was: "Îðê. Öàðèìèð" is "Орк. Царимир".
    ///     Latin-1 maps every byte to one character and back, so nothing was lost on the way in.
    /// </summary>
    /// <remarks>
    ///     ponytail: whatever <see cref="Garbled" /> flags is taken to be cp1251, since every one in this library
    ///     is. A Latin-1 tag that is more than half accented letters would come out as Cyrillic nonsense instead
    ///     of being dropped; a check against a Bulgarian letter frequency is the upgrade if one ever turns up.
    /// </remarks>
    /// <returns>The Cyrillic, or <c>null</c> when the value holds a character Latin-1 cannot: a U+FFFD.</returns>
    internal static string? Recovered(string value)
    {
        return value.All(character => character <= 'ÿ')
            ? Encoding.GetEncoding(1251).GetString(Encoding.Latin1.GetBytes(value))
            : null;
    }

    /// <summary>
    ///     A tag written in cp1251 and read back as Latin-1 ("Îðê. Öàðèìèð" for "Орк. Царимир"), or with a
    ///     U+FFFD where it could not be decoded at all. The first is what <see cref="Recovered" /> undoes.
    ///     The second says nothing the filename does not say better, and is not Latin so it would lead.
    /// </summary>
    /// <remarks>ponytail: half the letters in À–ÿ is the cut. Real Latin-1 text rarely gets near it.</remarks>
    internal static bool Garbled(string value)
    {
        var letters = value.Count(char.IsLetter);
        return value.Contains('�') ||
               letters > 0 && value.Count(character => character is >= 'À' and <= 'ÿ') * 2 > letters;
    }
}
