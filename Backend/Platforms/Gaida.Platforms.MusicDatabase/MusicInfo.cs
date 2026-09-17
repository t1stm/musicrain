using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Gaida.Core.Platforms;
using Gaida.Core.Utils;
using Gaida.Platforms.MusicDatabase.Manager;

namespace Gaida.Platforms.MusicDatabase;

/// <summary>Every string a song can be found by, cleaned once and cached on the entry.</summary>
public sealed record SearchVariants(string[] Titles, string[] Artists, IReadOnlySet<string> Tags);

/// <summary>Which shape of lyrics file sits beside a track. Serialized by name — see MusicInfo.SerializerOptions.</summary>
public enum LyricsKind { Unsynchronized, Synchronized }

/// <summary>Who found the words. Enums rather than strings: a file carrying "synced" should fail at load.</summary>
public enum LyricsOrigin { Deezer, [JsonStringEnumMemberName("LRCLIB")] Lrclib }

public class MusicInfo : IJsonOnDeserialized
{
    /// <summary>Layout of the per-artist Info.json files on disk. Property names are the on-disk names.</summary>
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string?[] _legacy = new string?[4];
    private SearchVariants? _search;

    [JsonPropertyName("ID")] public string? Id { get; set; }

    /// <summary>
    ///     Every reading of the name, original first. Later entries are alternates — a romanization when the
    ///     original is not Latin, the filename's or folder's spelling when it disagrees with the tag. Nothing
    ///     downstream picks a winner; matching compares them all.
    /// </summary>
    public List<string> Titles
    {
        get;
        set
        {
            field = value;
            _search = null;
        }
    } = [];

    /// <summary>
    ///     As <see cref="Titles" />. Compound names stay joined here; <see cref="TitleNormalizer.SplitArtists" /> splits
    ///     at match time.
    /// </summary>
    public List<string> Artists
    {
        get;
        set
        {
            field = value;
            _search = null;
        }
    } = [];

    public string? Album { get; set; }

    /// <summary>
    ///     Which tag-reading pass produced this entry. Entries below <see cref="MusicManager.ScanVersion" />
    ///     are re-read once and then stamped, so a tag the scanner learns to read later reaches the songs
    ///     that were indexed before it. Absent from an older file, which deserializes as 0.
    /// </summary>
    public int Scan { get; set; }

    /// <summary>
    ///     The absolute cover URL, as everything downstream wants it. Not serialized — see
    ///     <see cref="StoredCoverUrl" />.
    /// </summary>
    [JsonIgnore]
    public string? CoverUrl { get; set; }

    /// <summary>
    ///     What <c>CoverUrl</c> looks like on disk: the host is a <c>$[DOMAIN]</c> placeholder, which
    ///     <see cref="MusicManager.Load" /> substitutes on the way in.
    /// </summary>
    /// <remarks>
    ///     The substitution used to be one-way, which was harmless while only the loader wrote
    ///     <c>Info.json</c> — it writes entries it has not substituted yet. An admin edit saves an entry
    ///     that has been, and without this it would bake this host's domain into the library file, so
    ///     the covers would break the next time <c>DOMAIN</c> changed. Round-tripping it here is one
    ///     property instead of a rule every writer has to remember.
    /// </remarks>
    [JsonPropertyName("CoverUrl")]
    public string? StoredCoverUrl
    {
        get
        {
            var host = MusicManager.AlbumCoverLocation;

            // With DOMAIN unset that is a bare "/Album_Covers", which is a prefix of far too much to
            // go substituting blindly.
            return CoverUrl is null || MusicManager.Domain.Length == 0
                ? CoverUrl
                : CoverUrl.Replace(host, "$[DOMAIN]");
        }
        init => CoverUrl = value;
    }

    public string? RelativeLocation { get; set; }

    /// <summary>Which shape of lyrics sits beside the audio file, or <c>null</c> for none.</summary>
    /// <remarks>
    ///     The file is the authority; this is its index. <see cref="MusicManager.ReconcileLyrics" />
    ///     rewrites this and <see cref="LyricsSource" /> from what is actually on disk on every scan, so
    ///     an entry can never point at a .lrc someone deleted — including one stih wrote.
    /// </remarks>
    public LyricsKind? LyricsType { get; set; }

    /// <summary>
    ///     Where the file came from, as stih reported it. <c>null</c> beside a non-null
    ///     <see cref="LyricsType" /> means the file was already in the folder — nothing overwrites one of
    ///     those.
    /// </summary>
    public LyricsOrigin? LyricsSource { get; set; }

    /// <summary>
    ///     The day stih last reported finding nothing for this track. Set only on a real "not found":
    ///     a timeout or a 429 never reaches this pod at all.
    /// </summary>
    public DateOnly? LyricsChecked { get; set; }

    /// <summary>
    ///     Where a lyrics file of one kind would live, relative to the storage root. Relative because it is
    ///     what goes over the wire to stih, which has the same tree mounted at its own path.
    /// </summary>
    public string? LyricsPathFor(LyricsKind kind) => RelativeLocation is null
        ? null
        : Path.ChangeExtension(RelativeLocation, kind == LyricsKind.Synchronized ? ".lrc" : ".txt");

    // ponytail: read-only shim for the four-field format. Setter-only properties are never serialized by
    // System.Text.Json, so nothing writes these names back. Delete once no Info.json still carries them.
    [JsonPropertyName("OriginalTitle")]
    public string? LegacyOriginalTitle
    {
        set => _legacy[0] = value;
    }

    [JsonPropertyName("RomanizedTitle")]
    public string? LegacyRomanizedTitle
    {
        set => _legacy[1] = value;
    }

    [JsonPropertyName("OriginalAuthor")]
    public string? LegacyOriginalAuthor
    {
        set => _legacy[2] = value;
    }

    [JsonPropertyName("RomanizedAuthor")]
    public string? LegacyRomanizedAuthor
    {
        set => _legacy[3] = value;
    }

    /// <summary>Set when the entry was read in the four-field format, so the loader knows to re-read its tags.</summary>
    [JsonIgnore]
    public bool WasLegacy { get; private set; }

    [JsonIgnore] public TimeSpan Duration { get; set; }

    /// <summary>The name as tagged: the original.</summary>
    [JsonIgnore]
    public string? Title => Titles.FirstOrDefault();

    [JsonIgnore] public string? Artist => Artists.FirstOrDefault();

    /// <summary>What to show someone who cannot read the original script.</summary>
    [JsonIgnore]
    public string? DisplayTitle => Titles.FirstOrDefault(IsLatin) ?? Title;

    [JsonIgnore] public string? DisplayArtist => Artists.FirstOrDefault(IsLatin) ?? Artist;

    /// <summary>
    ///     Derived, never stored: a flag written at import time would be wrong wherever the transliteration
    ///     failed, and this cannot drift from the array it describes.
    /// </summary>
    [JsonIgnore]
    public bool ContainsRomanized => Titles.Count > 1 && !IsLatin(Titles[0]) && IsLatin(Titles[1]);

    [JsonIgnore] public SearchVariants Search => _search ??= BuildSearch();

    public double Length
    {
        get => Duration.TotalMilliseconds;
        set => Duration = TimeSpan.FromMilliseconds(value);
    }

    public void OnDeserialized()
    {
        // Original first regardless of the order the properties appear in the file.
        if (Titles.Count == 0 && Variants(_legacy[0], _legacy[1]) is { Count: > 0 } titles)
        {
            Titles = titles;
            WasLegacy = true;
        }

        if (Artists.Count == 0 && Variants(_legacy[2], _legacy[3]) is { Count: > 0 } artists)
        {
            Artists = artists;
            WasLegacy = true;
        }
    }

    /// <summary>Trims, romanizes what it can, drops blanks and duplicates. Order is preserved: the first value wins index 0.</summary>
    public static List<string> Variants(params string?[] values)
    {
        var result = new List<string>();
        foreach (var value in values)
        {
            var trimmed = value?.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            Add(result, trimmed);
            Add(result, Romanize.FromCyrillic(trimmed).Trim());
        }

        return result;
    }

    /// <summary>Appends the path-derived names behind whatever the tags already gave.</summary>
    public void AddNames(string? title, string? artist, string? folder)
    {
        Titles = Merge(Titles, title);
        Artists = Merge(Artists, artist, folder);
    }

    /// <summary>The tag reading, ahead of the names an older scan parsed out of the path.</summary>
    public void PreferTags(MusicInfo tagged)
    {
        Titles = Merge(tagged.Titles, [.. Titles]);
        Artists = Merge(tagged.Artists, [.. Artists]);
    }

    private static List<string> Merge(List<string> head, params string?[] tail)
    {
        var merged = new List<string>();
        foreach (var value in head) Add(merged, value);
        foreach (var value in Variants(tail)) Add(merged, value);

        return merged;
    }

    private static void Add(List<string> into, string value)
    {
        if (value.Length > 0 && !into.Contains(value, StringComparer.OrdinalIgnoreCase)) into.Add(value);
    }

    /// <summary>Latin Extended and its diacritics; everything above is Cyrillic, Greek, CJK or kana.</summary>
    private static bool IsLatin(string value)
    {
        return !value.Any(character => character > 'ͯ');
    }

    private SearchVariants BuildSearch()
    {
        var tags = new HashSet<string>(StringComparer.Ordinal);
        var titles = new List<string>();
        var artists = new List<string>();

        foreach (var title in Titles)
        {
            var (text, titleTags) = TitleNormalizer.NormalizeLibrary(title);
            tags.UnionWith(titleTags);
            Add(titles, LevenshteinDistance.RemoveFormatting(text) ?? string.Empty);
        }

        foreach (var name in Artists.SelectMany(TitleNormalizer.SplitArtists))
            Add(artists, LevenshteinDistance.RemoveFormatting(name) ?? string.Empty);

        return new SearchVariants([.. titles], [.. artists], tags);
    }

    public MusicResult ToMusicResult(IReadOnlyList<ContentGetter> getters)
    {
        return new MusicResult
        {
            Id = "audio://" + (Id ??= UpdateRandomId()),
            Downloaders = getters,
            Name = DisplayTitle,
            Artist = DisplayArtist,
            Album = Album,
            Duration = Duration,
            Path = MusicManager.StorageDirectory + "/" + RelativeLocation,
            ThumbnailUrl = CoverUrl,
            OriginalTitle = Title,
            OriginalArtist = Artist
        };
    }

    public string UpdateRandomId()
    {
        return $"{Prefix(Artists, 2)}{Prefix(Titles, 6)}-{Generation.RandomString(2)}";
    }

    /// <summary>IDs travel in URLs, so the prefix comes from a Latin variant — random when the song has none.</summary>
    private static string Prefix(List<string> variants, int length)
    {
        // A wholly Latin variant first: a mixed one like "До Вчера = Until Yesterday" cleans down to "---unt".
        var source = variants.FirstOrDefault(IsLatin)
                     ?? variants.FirstOrDefault(variant => variant.Any(char.IsAsciiLetterOrDigit))
                     ?? string.Empty;
        var clean = new string(source.Select(character => character == ' ' ? '-' : character)
            .Where(character => char.IsAsciiLetterOrDigit(character) || character == '-')
            .ToArray()).ToLower();

        return clean.Length >= length
            ? clean[..length]
            : clean + Generation.RandomString(length - clean.Length).ToLower();
    }
}