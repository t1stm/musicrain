using Gaida.Core.Utils;

namespace Gaida.Platforms.MusicDatabase.Manager;

public enum LocalMatchKind
{
    /// <summary>The same recording: the tag sets agree.</summary>
    Same,

    /// <summary>A tagged upload answered with the plain library copy.</summary>
    Variant,

    /// <summary>Close enough to offer, honest enough to say so.</summary>
    Weak
}

public sealed record LocalMatch(
    MusicInfo Song,
    LocalMatchKind Kind,
    double Score,
    TimeSpan DurationDelta,
    IReadOnlyList<string> YouTubeTags,
    IReadOnlyList<string> LibraryTags);

public partial class MusicManager
{
    /// <summary>A weak match has to agree on length; a strong one never has to, since uploads carry intros.</summary>
    private const double WeakDurationSeconds = 20;

    /// <summary>An unrecognised bracket is a small doubt, not a disqualification.</summary>
    private const double UnknownTagPenalty = 0.02;

    private const double TitleWeight = 0.65;

    private const double ArtistWeight = 0.35;

    // ponytail: calibration knobs, not constants of nature — re-run CalibrationTests over the library and
    // re-read the CSV whenever the library's tagging habits change. WeakMatch == StrongMatch switches the
    // weak band off with no branch to delete.
    //
    // Set from a 2000-title pass over the real library (3671 songs): every match at 0.806 and above was
    // right, and the wrong answers start at 0.783 (Rammstein's "Heirate Mich" answered with "Bestrafe
    // mich"), so strong sits just above them. Below that, with the 20-second gate on, the band down to
    // 0.72 runs roughly eight right to three wrong — honest enough for a prompt that says "possibly".
    public static double StrongMatch = 0.80;
    public static double WeakMatch = 0.72;

    /// <summary>
    ///     The library's answer to a YouTube result: the same recording, a plain copy of a tagged upload, or
    ///     nothing. Touches only the in-memory song list — no platform call, so it is cheap enough to run
    ///     after every roll.
    /// </summary>
    /// <param name="name">The video title, junk and all.</param>
    /// <param name="artist">The channel title.</param>
    /// <param name="duration">The upload's length, reported back as a delta and never used to reject a strong match.</param>
    public LocalMatch? FindLocalVariant(string name, string? artist, TimeSpan duration)
    {
        var normalized = TitleNormalizer.Normalize(name, artist);
        var candidates = normalized.Candidates
            .Select(candidate => (
                Artist: LevenshteinDistance.RemoveFormatting(candidate.Artist) ?? string.Empty,
                Title: LevenshteinDistance.RemoveFormatting(candidate.Title) ?? string.Empty))
            .Where(candidate => candidate.Title.Length > 0)
            .Distinct()
            .ToArray();

        if (candidates.Length == 0)
        {
            Logger.Information("MusicManager: No usable candidates in video title: {Name}", name);
            return null;
        }

        var youTubeTags = Ordered(normalized.RenditionTags);

        // ponytail: linear scan × ~6 candidates over the whole library, same cost shape as SearchByTerm.
        // Prefilter on a first-letter or length bucket if the library outgrows it.
        var best = Songs.AsParallel()
            .Select(song =>
            {
                var (score, tags) = Score(song, candidates);
                return (Song: song, Score: score, Tags: Ordered(tags));
            })
            // Renditions run one way only: an untagged library track answers anything, a tagged one answers
            // only an upload carrying the same tags. Filtering before the pick means a tagged best-scorer
            // does not hide the untagged runner-up that was offerable all along.
            .Where(scored => scored.Tags.SequenceEqual(youTubeTags) || scored.Tags.Count == 0)
            .OrderByDescending(scored => scored.Score)
            .FirstOrDefault();

        if (best.Song is null) return null;

        var score = best.Score - normalized.UnknownTags * UnknownTagPenalty;
        var delta = best.Song.Duration - duration;
        var kind = best.Tags.SequenceEqual(youTubeTags) ? LocalMatchKind.Same : LocalMatchKind.Variant;

        if (score >= StrongMatch)
            return new LocalMatch(best.Song, kind, score, delta, youTubeTags, best.Tags);

        if (score >= WeakMatch && Math.Abs(delta.TotalSeconds) <= WeakDurationSeconds)
            return new LocalMatch(best.Song, LocalMatchKind.Weak, score, delta, youTubeTags, best.Tags);

        Logger.Debug("MusicManager: Best local variant for {Name} scored {Score}, below the bar", name, score);
        return null;
    }

    /// <summary>
    ///     Weighted so neither half carries a match alone: a perfect artist against a wrong title tops out at
    ///     0.35, a perfect title against a wrong artist at 0.65. Both are below any usable threshold, which is
    ///     the guard against a library full of one artist matching everything they ever released.
    /// </summary>
    private static (double Score, IReadOnlySet<string> Tags) Score(MusicInfo song,
        (string Artist, string Title)[] candidates)
    {
        var (titles, artists, tags) = song.Search;
        if (titles.Length == 0) return (0, tags);

        var best = (from candidate in candidates
                let titleSimilarity = titles.Max(title => Similarity(candidate.Title, title))
                select candidate.Artist.Length == 0
                    ? Math.Max(TitleWeight * titleSimilarity, Concatenated(candidate.Title, titles, artists))
                    : TitleWeight * titleSimilarity +
                      ArtistWeight * artists.Select(a => Similarity(candidate.Artist, a)).DefaultIfEmpty(0).Max())
            .Prepend(0d)
            .Max();

        return (best, tags);
    }

    private static double Concatenated(string term, string[] titles, string[] artists)
    {
        var best = 0d;
        foreach (var title in titles)
        foreach (var artist in artists)
        {
            best = Math.Max(best, Similarity(term, title + artist));
            best = Math.Max(best, Similarity(term, artist + title));
        }

        return best;
    }

    /// <summary>
    ///     Length-relative, unlike <c>SearchByTerm</c>'s absolute budget: a long transliterated title
    ///     accumulates several ambiguous letters (ю→yu|iu, я→ya|ia, щ→sht|sh) and a fixed distance rejects it.
    /// </summary>
    private static double Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        return 1d - (double)LevenshteinDistance.ComputeStrict(a, b) / Math.Max(a.Length, b.Length);
    }

    private static IReadOnlyList<string> Ordered(IEnumerable<string> tags)
    {
        return [.. tags.OrderBy(tag => tag, StringComparer.Ordinal)];
    }
}
