namespace Gaida.Platforms.MusicDatabase.Manager;

/// <summary>
///     Everything the library knows about lyrics. It does not fetch them and has never heard of LRCLIB:
///     stih writes the files onto the shared volume, and these three methods are how the two agree on
///     what is there — see LYRICS_1_LIBRARY_PLAN.md.
/// </summary>
public partial class MusicManager
{
    /// <summary>
    ///     What is actually on disk beside one track, written back onto the entry. Two File.Exists calls
    ///     and nothing else — cheap enough for every song on every scan, which is what makes the file the
    ///     authority rather than the index.
    /// </summary>
    /// <returns><c>true</c> when the entry changed and its folder needs saving.</returns>
    internal static bool ReconcileLyrics(MusicInfo entry)
    {
        // Synchronized wins when both exist: a timed file contains the plain words too. The .txt is left
        // where it is — deleting a listener's file is not this method's business.
        var found = Exists(entry, LyricsKind.Synchronized) ? LyricsKind.Synchronized
            : Exists(entry, LyricsKind.Unsynchronized) ? LyricsKind.Unsynchronized
            : (LyricsKind?)null;

        if (found == entry.LyricsType) return false;

        // The provenance belonged to the file that was recorded, so it goes with it. A file that appeared
        // without a stamp has unknown provenance, which is exactly the null-source state nothing
        // overwrites. LyricsChecked is left alone: someone deleting a bad .lrc should not trigger an
        // immediate re-fetch of the same bad .lrc — the retry window reopens on its own.
        entry.LyricsType = found;
        entry.LyricsSource = null;
        return true;
    }

    private static bool Exists(MusicInfo entry, LyricsKind kind)
    {
        return entry.LyricsPathFor(kind) is { } relative &&
               File.Exists(Path.Combine(StorageDirectory, relative));
    }

    /// <summary>Records what stih found, or that it found nothing. Never overwrites existing lyrics.</summary>
    /// <param name="kind">What was written beside the audio, or <c>null</c> for "looked, found nothing".</param>
    /// <param name="source">Who found it. <c>null</c> means the file was already in the folder.</param>
    /// <remarks>
    ///     It writes no lyrics file — stih has already done that on the shared volume, and this call only
    ///     records what it did. Under the edit gate for the same reason <see cref="EditAsync" /> is: both
    ///     rewrite one entry and then its folder's Info.json.
    /// </remarks>
    public async Task<(MusicInfo? entry, string? error)> StampLyricsAsync(string id, LyricsKind? kind,
        LyricsOrigin? source)
    {
        await editGate.WaitAsync();

        try
        {
            var entry = SearchById(id);
            if (entry is null) return (null, "No song with that ID.");
            if (entry.RelativeLocation is null) return (null, "That entry has no file on disk.");
            if (entry.LyricsType is not null) return (null, "That entry already has lyrics.");

            entry.LyricsType = kind;
            entry.LyricsSource = kind is null ? null : source;
            if (kind is null) entry.LyricsChecked = DateOnly.FromDateTime(DateTime.UtcNow);

            await SaveFolderAsync(entry.RelativeLocation);
            Logger.Information("Stamped lyrics on {Id}: {Kind} from {Source}", id, kind, source);

            return (entry, null);
        }
        finally
        {
            editGate.Release();
        }
    }

    /// <summary>Tracks with no lyrics beside them, oldest-checked first, for stih's sweep.</summary>
    /// <param name="retryBefore">A miss recorded before this day is offered again; LRCLIB grows.</param>
    public IReadOnlyList<MusicInfo> MissingLyrics(int take, DateOnly retryBefore)
    {
        // Nulls first, so tracks nobody has tried come before retries. Then the ID, for a stable page.
        return
        [
            .. Songs.Where(song => song.RelativeLocation is not null
                                   && song.LyricsType is null
                                   && (song.LyricsChecked is null || song.LyricsChecked < retryBefore))
                .OrderBy(song => song.LyricsChecked ?? DateOnly.MinValue)
                .ThenBy(song => song.ID, StringComparer.Ordinal)
                .Take(Math.Clamp(take, 1, 500))
        ];
    }
}
