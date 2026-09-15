using Gaida.Core.Utils;

namespace Stih;

/// <summary>
///     Which LRCLIB candidate is this track, if any.
/// </summary>
/// <remarks>
///     <c>/api/search</c> returns live versions, covers and the same song under three albums. The filter
///     is the one the library already uses — the same <see cref="TitleNormalizer" />, the same
///     <see cref="LevenshteinDistance" />, the same 0.65/0.35 weights and the same 0.80 bar — so a lyrics
///     match and a search hit agree about what a track is.
///     <para>
///         A weak lyrics match is worse than none: wrong words scrolling in time with the music is the
///         one failure a listener cannot ignore. So there is no weak band here, unlike the library's
///         matcher, which offers one because a wrong song is recoverable by pressing skip.
///     </para>
///     Pure: no I/O, no logger, no HTTP types.
/// </remarks>
public static class Matching
{
    /// <summary>The library's own bar — <c>MusicManager.StrongMatch</c>. Below it, nothing is offered.</summary>
    public const double StrongMatch = 0.80;

    private const double TitleWeight = 0.65;
    private const double ArtistWeight = 0.35;

    /// <summary>
    ///     LRCLIB reports whole seconds and the library reports the tag's own length, which disagree by
    ///     rounding on nearly every track. One second either way is the specified gate, and it is what
    ///     throws out the live version that shares a title.
    /// </summary>
    public const double DurationToleranceSeconds = 1;

    /// <summary>
    ///     The candidate that is this track, or <c>null</c> when none of them is.
    /// </summary>
    /// <param name="track">What the owning pod says is playing.</param>
    /// <param name="candidates">Everything <c>/api/search</c> answered, unfiltered.</param>
    public static LrcLibResult? Best(Track track, IReadOnlyList<LrcLibResult> candidates)
    {
        var (title, tags) = Normalized(track.Title);
        var artists = Artists(track.Artist);
        if (title.Length == 0) return null;

        var scored = new List<(LrcLibResult Result, double Score, double Delta)>();
        foreach (var candidate in candidates)
        {
            if (candidate.Instrumental) continue;
            if (candidate.PlainLyrics is null && candidate.SyncedLyrics is null) continue;

            // The length gate first: it is one subtraction and it removes most of the field.
            var delta = Math.Abs((candidate.Duration ?? -1) - track.Duration.TotalSeconds);
            if (track.Duration > TimeSpan.Zero && delta > DurationToleranceSeconds) continue;

            var (candidateTitle, candidateTags) = Normalized(candidate.TrackName);
            if (candidateTitle.Length == 0) continue;

            // A (Live) result for a plain track is the wrong recording however well the names score.
            if (!candidateTags.SetEquals(tags)) continue;

            var score = TitleWeight * Similarity(title, candidateTitle) +
                        ArtistWeight * BestArtist(artists, candidate.ArtistName);

            if (score >= StrongMatch) scored.Add((candidate, score, delta));
        }

        // Synchronized first — a timed file contains the plain words too, so it is never the worse
        // answer — then the smallest length delta, then the better score.
        return scored
            .OrderByDescending(entry => entry.Result.SyncedLyrics is not null)
            .ThenBy(entry => entry.Delta)
            .ThenByDescending(entry => entry.Score)
            .Select(entry => entry.Result)
            .FirstOrDefault();
    }

    /// <summary>
    ///     Whether an <c>/api/get</c> answer is usable. That route matched on length and names itself, so
    ///     this only rejects what has no words in it at all.
    /// </summary>
    public static bool IsUsable(LrcLibResult? result)
    {
        return result is { Instrumental: false } && (result.PlainLyrics is not null || result.SyncedLyrics is not null);
    }

    /// <summary>The same cleaning <c>MusicInfo.BuildSearch</c> does, so both sides compare like for like.</summary>
    private static (string Text, IReadOnlySet<string> Tags) Normalized(string? title)
    {
        var (text, tags) = TitleNormalizer.NormalizeLibrary(title);
        return (LevenshteinDistance.RemoveFormatting(text) ?? string.Empty, tags);
    }

    private static string[] Artists(string? artist)
    {
        return [.. TitleNormalizer.SplitArtists(artist)
            .Select(name => LevenshteinDistance.RemoveFormatting(name) ?? string.Empty)
            .Where(name => name.Length > 0)];
    }

    /// <summary>
    ///     A track with no artist tag scores the artist half as a match rather than as a miss: the length
    ///     gate and the title have already done the work, and zeroing it would put every such track below
    ///     the bar whatever LRCLIB said.
    /// </summary>
    private static double BestArtist(string[] artists, string? candidate)
    {
        if (artists.Length == 0) return 1;

        return Artists(candidate)
            .SelectMany(_ => artists, (theirs, ours) => Similarity(ours, theirs))
            .DefaultIfEmpty(0)
            .Max();
    }

    /// <summary>
    ///     Length-relative, like the library's: a long transliterated title accumulates several ambiguous
    ///     letters and a fixed distance rejects it.
    /// </summary>
    private static double Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        return 1d - (double)LevenshteinDistance.ComputeStrict(a, b) / Math.Max(a.Length, b.Length);
    }
}
