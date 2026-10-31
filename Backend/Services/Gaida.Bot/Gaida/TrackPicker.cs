using System.Runtime.CompilerServices;
using Gaida.Bot.Tools;

namespace Gaida.Bot.Gaida;

/// <summary>Which tracks a play command queues for what was typed.</summary>
public static class TrackPicker
{
    /// <summary>
    /// What a query puts in the queue: every track of a playlist, the one track a link names, or
    /// the single best hit for a search term. Only a playlist queues more than one thing.
    /// </summary>
    public static async IAsyncEnumerable<Track> TracksForAsync(this GaidaClient api, string search,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var resolution = await api.ResolveAsync(search, cancellationToken);

        // A link the resolver already turned into a track: no search needed at all.
        if (resolution?.Result is not null)
        {
            yield return resolution.Result;
            yield break;
        }

        if (resolution?.IsPlaylist is true)
        {
            await foreach (var track in api.SearchAsync(resolution.Query, cancellationToken)) yield return track;
            yield break;
        }

        // Ordinary text, or a resolver that could not answer. Search streams its hits in the order
        // the pods answer, not by relevance, so every hit has to be in before one can be picked.
        var results = new List<Track>();
        await foreach (var track in api.SearchAsync(resolution?.Query ?? search, cancellationToken)) results.Add(track);

        var best = BestMatch(results, search);
        if (best is not null) yield return best;
    }

    /// <summary>
    /// The closest hit by edit distance against the title and against the title and artist in
    /// either order, with the platform as the tie-break: the library, then Deezer, then YouTube.
    /// A library hit is scored under every name the library knows it by, not only the one shown.
    /// </summary>
    /// <remarks>
    /// The artist on its own is deliberately not a candidate. Scoring it would let every track by
    /// the right artist tie with the one that was actually asked for, and "rammstein sonne" would
    /// come back with whichever Rammstein track a pod happened to answer with first.
    /// </remarks>
    public static Track? BestMatch(List<Track> results, string search)
    {
        var term = search.Trim();

        return results
            .OrderBy(track => Distance(track, term))
            .ThenBy(track => track.Id switch
            {
                _ when track.Id.StartsWith(Track.LocalPrefix, StringComparison.Ordinal) => 0,
                _ when track.Id.StartsWith(Track.DeezerPrefix, StringComparison.Ordinal) => 1,
                _ => 2
            })
            .FirstOrDefault();
    }

    /// <remarks>
    /// The variants are what made the library answer at all: "skase skase" finds "Σκάσε" through its
    /// alternate title "Skase, skase", which the displayed name alone sits six edits from — further
    /// than an unrelated "Skade Skade".
    /// </remarks>
    private static int Distance(Track track, string term)
    {
        string[] titles = [track.Name, .. track.TitleVariants ?? []];
        string[] artists = [track.Artist, .. track.ArtistVariants ?? []];

        return titles.Min(title => artists.Min(artist => Math.Min(
            LevenshteinDistance.ComputeLean(title, term),
            Math.Min(LevenshteinDistance.ComputeLean($"{title} - {artist}", term),
                LevenshteinDistance.ComputeLean($"{artist} - {title}", term)))));
    }
}
