using System.Text.RegularExpressions;

namespace Gaida.Core.Utils;

/// <summary>One (artist, title) reading of a video title. An empty artist means the split found none.</summary>
public readonly record struct TitleCandidate(string Artist, string Title);

/// <summary>
///     A messy video title turned into clean candidates, plus what the brackets said about it:
///     <paramref name="RenditionTags" /> is what makes a match a different take rather than the same recording.
/// </summary>
public sealed record NormalizedTitle(
    IReadOnlyList<TitleCandidate> Candidates,
    IReadOnlySet<string> RenditionTags,
    int UnknownTags);

/// <summary>Pure. No state, no I/O.</summary>
public static partial class TitleNormalizer
{
    /// <summary>
    ///     How short a segment may be against the longest one and still count as its script twin. A script pair is
    ///     close to even by construction; a separator inside a name leaves one side a fragment.
    /// </summary>
    // ponytail: calibration knob, same as MusicManager's strong/weak. 0.54 is the 65/35 split from the plan.
    private static readonly double BalanceRatio = 0.54;

    /// <summary>Dropped from the text, no effect on the verdict. A remaster is the same performance, so it sits here.</summary>
    private static readonly string[] Noise =
    [
        "official video", "official audio", "official music video", "music video", "official",
        "video", "audio", "lyrics", "lyric video", "lyric", "visualizer", "visualiser",
        "mv", "hd", "hq", "4k", "feat", "ft", "featuring", "prod", "subtitles", "remastered", "remaster"
    ];

    /// <summary>Dropped from the text and recorded: a different take on the same song.</summary>
    private static readonly string[] Rendition =
    [
        "instrumental", "live", "acoustic", "remix", "radio edit", "extended", "cover",
        "karaoke", "demo", "unplugged", "sped up", "slowed", "clean", "8d"
    ];

    private static readonly HashSet<string> NoiseWords =
        new(Noise.SelectMany(phrase => phrase.Split(' ')).Append("by"), StringComparer.Ordinal);

    /// <summary>Turns a YouTube video title and its channel title into the candidates worth scoring.</summary>
    public static NormalizedTitle Normalize(string? videoTitle, string? channelTitle = null)
    {
        var tags = new HashSet<string>(StringComparer.Ordinal);
        var text = StripTags(StripYear(videoTitle ?? string.Empty), tags, out var unknown);
        var channel = CleanChannel(channelTitle);

        var candidates = new List<TitleCandidate>();
        foreach (var segment in SegmentTexts(text))
            AddCandidates(candidates, StripYear(segment), channel);

        return new NormalizedTitle(candidates, tags, unknown);
    }

    /// <summary>
    ///     The library side of the same classifier: a library <c>(Instrumental)</c> has to come off the title before
    ///     the two can be compared at all, and is the tag the verdict weighs against the upload's.
    /// </summary>
    public static (string Text, IReadOnlySet<string> Tags) NormalizeLibrary(string? title)
    {
        var tags = new HashSet<string>(StringComparer.Ordinal);
        return (StripTags(StripYear(title ?? string.Empty), tags, out _), tags);
    }

    /// <summary>
    ///     "Doni &amp; Momchil" → ["Doni &amp; Momchil", "Doni", "Momchil"]. The joined form stays first: an exact
    ///     tag match has to outrank a match on one of the names.
    /// </summary>
    public static IReadOnlyList<string> SplitArtists(string? artist)
    {
        var whole = Collapse(artist ?? string.Empty);
        if (whole.Length == 0) return [];

        var parts = ArtistSeparatorRegex().Split(whole)
            .Select(Collapse)
            .Where(part => part.Length > 1)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (parts.Count < 2) return [whole];

        parts.Insert(0, whole);
        return [.. parts.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    // §A. Anchored to end-of-string and requiring a comma or a bracket, so "ARE, DARPAY" keeps its comma
    // and a song actually called "1979" keeps its name.
    private static string StripYear(string text)
    {
        var stripped = TrailingYearRegex().Replace(text, string.Empty).Trim();
        return stripped.Length == 0 ? text.Trim() : stripped;
    }

    // §B. Everything bracketed leaves the search text; what it was is what the verdict turns on.
    private static string StripTags(string text, HashSet<string> tags, out int unknown)
    {
        var unknownCount = 0;
        var cleaned = BracketRegex().Replace(text, match =>
        {
            var tag = Classify(match.Groups[1].Value);
            if (tag is null) unknownCount++;
            else if (tag.Length > 0) tags.Add(tag);
            return " ";
        });

        unknown = unknownCount;
        return Collapse(cleaned);
    }

    /// <returns>The rendition tag, <c>""</c> for noise, <c>null</c> for an unrecognised bracket.</returns>
    private static string? Classify(string content)
    {
        var words = $" {NonWordRegex().Replace(content, " ").Trim().ToLowerInvariant()} ";
        if (words.Length <= 2) return string.Empty;

        // Rendition wins a tie: "(Official Live Video)" is a live recording that also happens to be official.
        foreach (var tag in Rendition)
            if (words.Contains($" {tag} ", StringComparison.Ordinal))
                return tag;

        return Noise.Any(word => words.Contains($" {word} ", StringComparison.Ordinal)) ? string.Empty : null;
    }

    // §C. Split dual-script titles, when the halves balance.
    private static List<string> SegmentTexts(string text)
    {
        // The capture group keeps the separators, so a title that does not split can be put back together
        // with the one it actually used instead of a guessed slash.
        var tokens = SeparatorRegex().Split(text);
        var parts = new List<string>();
        var separators = new List<string>();

        for (var i = 0; i < tokens.Length; i += 2)
        {
            var segment = Collapse(tokens[i]);
            // The pipe carries metadata far more often than a script twin, so noise segments leave before
            // anything is weighed — same vocabulary, same classifier.
            if (segment.Length == 0 || IsAllNoise(segment)) continue;

            if (parts.Count > 0) separators.Add(i > 0 ? tokens[i - 1] : "/");
            parts.Add(segment);
        }

        if (parts.Count == 0) return [Collapse(text)];
        if (parts.Count == 1) return parts;

        var longest = parts.Max(part => part.Length);
        var kept = parts.Where(part => (double)part.Length / longest >= BalanceRatio).ToList();
        if (kept.Count >= 2) return kept;

        // Lopsided: the separator was inside a name. The unsplit title is the only candidate — no junk
        // "AC" left in the pool to trip the threshold against something unrelated.
        return [string.Join(string.Empty, parts.Select((part, i) => i == 0 ? part : separators[i - 1] + part))];
    }

    private static bool IsAllNoise(string segment)
    {
        var words = NonWordRegex().Split(segment.ToLowerInvariant()).Where(word => word.Length > 0).ToArray();
        return words.Length > 0 && words.All(NoiseWords.Contains);
    }

    // §D. " - " is the separator, the same convention MusicManager.ParseFile reads filenames by. Which side is
    // the artist is the uploader's business, so both readings are emitted rather than guessed at.
    private static void AddCandidates(List<TitleCandidate> into, string text, string channel)
    {
        var index = text.IndexOf(" - ", StringComparison.Ordinal);
        if (index < 0)
        {
            Add(into, channel, text);
            Add(into, string.Empty, text);
            return;
        }

        var left = text[..index].Trim();
        var right = text[(index + 3)..].Trim();

        Add(into, left, right);
        Add(into, right, left);
        Add(into, channel, right);
    }

    // §E. A Cyrillic-only title only reaches a library entry through its RomanizedTitle.
    private static void Add(List<TitleCandidate> into, string artist, string title)
    {
        Push(into, artist, title);
        if (HasCyrillic(artist) || HasCyrillic(title))
            Push(into, Romanize.FromCyrillic(artist), Romanize.FromCyrillic(title));
    }

    private static void Push(List<TitleCandidate> into, string artist, string title)
    {
        if (title.Length == 0) return;

        var candidate = new TitleCandidate(artist, title);
        if (!into.Contains(candidate)) into.Add(candidate);
    }

    private static bool HasCyrillic(string text)
    {
        foreach (var character in text)
            if (character is >= 'Ѐ' and <= 'ӿ')
                return true;

        return false;
    }

    /// <summary>YouTube's auto-generated artist channels are the cleanest artist string there is, once labelled.</summary>
    private static string CleanChannel(string? channelTitle)
    {
        var text = Collapse(channelTitle ?? string.Empty);
        if (text.EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase)) text = text[..^8];
        text = VevoRegex().Replace(text, string.Empty);
        if (text.EndsWith("Official", StringComparison.OrdinalIgnoreCase)) text = text[..^8];

        return Collapse(text);
    }

    private static string Collapse(string text)
    {
        return WhitespaceRegex().Replace(text, " ").Trim().Trim('-', '–', '—').Trim();
    }

    [GeneratedRegex(@"(?:\s*,\s*\d{4}|\s*[(\[]\s*\d{4}\s*[)\]])\s*$")]
    private static partial Regex TrailingYearRegex();

    [GeneratedRegex(@"[(\[]([^)\]]*)[)\]]")]
    private static partial Regex BracketRegex();

    // Dashes are deliberately absent: they separate artist from title, which is §D's job.
    [GeneratedRegex(@"([/|\\~／｜∕⁄〜])")]
    private static partial Regex SeparatorRegex();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NonWordRegex();

    [GeneratedRegex(@"\s*VEVO\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex VevoRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    // ponytail: the separator set is measured off the library, not guessed. "и" is the Bulgarian "and"
    // (33 entries). "/", " x ", " vs " and " with " score zero there and are all substrings of real names,
    // so they stay out — add one when a real query misses. The whitespace around & is load-bearing:
    // "Rad&Co" is a band, "Слави Трифонов & Ку-ку бенд" is two artists.
    [GeneratedRegex(@"\s+(?:&|\+|и|featuring|feat\.?|ft\.?)\s+|\s*,\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ArtistSeparatorRegex();
}
