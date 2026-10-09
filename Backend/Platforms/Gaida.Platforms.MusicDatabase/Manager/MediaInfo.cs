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
    }

    /// <summary>One file, with the settings above applied. Pictures are read only when asked for.</summary>
    public static Track Read(string location)
    {
        return new Track(location);
    }

    public static MusicInfo GetInformation(string location)
    {
        var track = Read(location);
        var artists = track.AdditionalFields
            .FirstOrDefault(field => field.Key.Equals("ARTISTS", StringComparison.OrdinalIgnoreCase)).Value;

        return new MusicInfo
        {
            Id = string.Empty,
            Length = track.DurationMs,
            Titles = MusicInfo.Variants(Tag(track.Title)),
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

    /// <summary>A tag worth reading, or <c>null</c> for an absent or garbled one.</summary>
    private static string? Tag(string? value)
    {
        return string.IsNullOrEmpty(value) || Garbled(value) ? null : value;
    }

    /// <summary>
    ///     A tag written in cp1251 and read back as Latin-1 ("Îðê. Öàðèìèð" for "Орк. Царимир"), or with a
    ///     U+FFFD where it could not be decoded at all. Either one says nothing the filename does not say better,
    ///     and the first is not Latin so it would lead.
    /// </summary>
    /// <remarks>ponytail: half the letters in À–ÿ is the cut. Real Latin-1 text rarely gets near it.</remarks>
    internal static bool Garbled(string value)
    {
        var letters = value.Count(char.IsLetter);
        return value.Contains('�') ||
               letters > 0 && value.Count(character => character is >= 'À' and <= 'ÿ') * 2 > letters;
    }
}
