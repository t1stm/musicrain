using Gaida.Core.Utils;

namespace Gaida.Platforms.MusicDatabase.Manager;

/// <summary>What an operator changes: one song's names, and a track imported from elsewhere.</summary>
public partial class MusicManager
{
    /// <summary>
    ///     Rewrites one song's names and album, and saves the folder it lives in.
    /// </summary>
    /// <param name="titles">Replaces every title variant. <c>null</c> leaves them alone; empty is rejected.</param>
    /// <param name="artists">Replaces every artist variant. <c>null</c> leaves them alone.</param>
    /// <param name="album">Trimmed; the empty string clears it, <c>null</c> leaves it alone.</param>
    /// <returns>The updated entry, or an error naming what was wrong with the request.</returns>
    /// <remarks>
    ///     What an operator types is taken literally: unlike the import path, this does not add
    ///     romanizations of its own. They are welcome as extra variants, but a person editing a name is
    ///     the authority on it and should not find a line they never wrote appearing underneath.
    ///     <para>
    ///         The ID is deliberately <b>not</b> regenerated, though it is derived from these very
    ///         fields. It is the handle every playlist snapshot, every Dunav cache key and every link
    ///         already holds; re-rolling it on a typo fix would orphan all of them. <c>RereadTags</c>
    ///         regenerates because a bulk migration has no such links to keep.
    ///     </para>
    /// </remarks>
    /// <param name="id">The entry to edit. Kept as-is — see the remark on regeneration.</param>
    public async Task<Change> EditAsync(string id, IReadOnlyList<string>? titles,
        IReadOnlyList<string>? artists, string? album, CancellationToken cancellationToken = default)
    {
        await _editGate.WaitAsync(cancellationToken);

        try
        {
            var entry = SearchById(id);
            if (entry is null) return Change.NoSuchSong;
            if (entry.RelativeLocation is null) return Change.Refused("That entry has no file on disk.");

            if (titles is not null)
            {
                var cleaned = Distinct(titles);
                if (cleaned.Count == 0) return Change.Refused("A song needs at least one title.");
                entry.Titles = cleaned;
            }

            if (artists is not null) entry.Artists = Distinct(artists);
            if (album is not null) entry.Album = album.Trim() is { Length: > 0 } name ? name : null;

            _database.Write([entry]);
            Logger.Information("Admin edited {ID}: {Title} — {Artist}", id, entry.Title, entry.Artist);

            return entry;
        }
        finally
        {
            _editGate.Release();
        }
    }

    /// <summary>The top-level folder every imported track lands under, relative to the storage root.</summary>
    /// <remarks>
    ///     It sits where a genre sits in the rest of the tree — <c>Eurobeat/Lou Grant/Lou Grant - ....wv</c>
    ///     — because that is what the library is organised by and "where it came from" is the only honest
    ///     answer available at import time. An operator moving the artist folder under a real genre is a
    ///     move, not a re-import.
    /// </remarks>
    private const string ImportFolder = "Deezer";

    /// <summary>The name an artist folder gets when the source gave no usable artist at all.</summary>
    private const string UnknownArtistFolder = "Unknown artist";

    /// <summary>
    ///     Writes one downloaded track under <see cref="ImportFolder" /> and indexes it, so it becomes an
    ///     ordinary <c>audio://</c> library song.
    /// </summary>
    /// <remarks>
    ///     The layout is the library's own, one level shallower: <c>Deezer/&lt;artist&gt;/&lt;artist&gt; -
    ///     &lt;title&gt;.&lt;ext&gt;</c>. No album folder, because nothing in this tree has one — the level
    ///     between genre and artist that some entries carry is a sub-genre, and the album lives in the tags.
    ///     Putting the artist in its own folder is not cosmetic: <see cref="ParseFile" /> reads the
    ///     containing folder as an artist variant, so a file sitting directly in <c>Deezer/</c> is indexed
    ///     with "Deezer" as one of its artists.
    ///     <para>
    ///         Nothing else is guessed: the file is parsed by exactly the code a scan would have used, so an
    ///         import and a file dropped in by hand produce the same entry. The names the source supplied
    ///         are only the fallback, for a download whose tags say nothing.
    ///     </para>
    ///     <para>
    ///         An existing file of the same name is refused rather than overwritten. The admin is meant to
    ///         tidy these entries afterwards, and silently replacing a file underneath an entry someone had
    ///         already renamed is the one outcome that cannot be undone from the editor.
    ///     </para>
    /// </remarks>
    /// <param name="artist">The performer, as the entry's folder and artist list will read.</param>
    /// <param name="title">The track title, as the entry and its filename will read.</param>
    /// <param name="album">The album, or <c>null</c> when the source names none.</param>
    /// <param name="extension">Including the dot; must be one this library plays.</param>
    /// <param name="content">The audio itself, read to the end.</param>
    /// <param name="cover">
    ///     Artwork to fall back on when the file carries none embedded — a Deezer FLAC usually does not.
    ///     Stored and hashed exactly like an extracted cover, so the library holds its own copy.
    /// </param>
    /// <param name="cancellationToken">Cancels the read and the import.</param>
    /// <returns>The indexed entry, or an error naming what stopped it.</returns>
    public async Task<Change> ImportAsync(string artist, string title, string? album,
        string extension, Stream content, byte[]? cover = null, CancellationToken cancellationToken = default)
    {
        var cleanArtist = CleanForFilename(artist);
        var cleanTitle = CleanForFilename(title);
        if (cleanTitle.Length == 0) return Change.Refused("The track has no usable title.");
        if (!AudioFiles.IsAudio(extension)) return Change.Refused($"'{extension}' is not a playable extension.");

        var folder = cleanArtist.Length == 0 ? UnknownArtistFolder : cleanArtist;
        var directory = Path.Combine(options.StorageDirectory, ImportFolder, folder);
        var filename = $"{folder} - {cleanTitle}{extension}";
        var location = $"{directory}/{filename}";

        await _editGate.WaitAsync(cancellationToken);

        try
        {
            Directory.CreateDirectory(directory);
            if (File.Exists(location))
                return Change.Refused($"'{filename}' is already in the {ImportFolder} folder.");

            await WriteWholeAsync(content, location, cancellationToken);

            var entry = ParseFile(location);
            entry.Album ??= album?.Trim() is { Length: > 0 } named ? named : null;
            if (CoverFor(location, cover) is { } artwork) entry.CoverUrl = $"{options.AlbumCoverLocation}/{artwork}";

            // Copy-on-write rather than Add: SearchById and Browse read this list from other threads and
            // from AsParallel, and growing it underneath them is the classic torn-enumeration crash.
            Songs = [.. Songs, entry];
            _database.Write([entry]);

            Logger.Information("Imported {Title} - {Artist} as {Location}", entry.Title, entry.Artist, location);
            return entry;
        }
        finally
        {
            _editGate.Release();
        }
    }

    /// <summary>
    ///     Temp then move: a download that dies halfway must not leave a truncated file behind, because the next
    ///     scan would index it as a whole song.
    /// </summary>
    private static async Task WriteWholeAsync(Stream content, string location, CancellationToken cancellationToken)
    {
        var temporary = location + ".part";
        await using (var file = File.Create(temporary))
        {
            await content.CopyToAsync(file, cancellationToken);
        }

        File.Move(temporary, location);
    }

    /// <summary>
    ///     The file's own artwork first, the source's only when it has none. The caller stores the substituted
    ///     form, not the $[DOMAIN] placeholder: the entry goes straight into the in-memory library, and
    ///     LibraryOptions.Stored puts the placeholder back on the way to disk.
    /// </summary>
    /// <returns>The stored cover's file name, or <c>null</c> when there is none to keep.</returns>
    private string? CoverFor(string location, byte[]? cover)
    {
        return _coverExtractor.ExportCover(location) ??
               (cover is { Length: > 0 } ? _coverExtractor.StoreCover(cover) : null);
    }

    /// <summary>
    ///     A name that is safe as a filename and still round-trips through <see cref="ParseFile" />: it splits
    ///     on '/' for the folder and on " - " for the artist, so neither may survive into the name.
    /// </summary>
    private static string CleanForFilename(string? value)
    {
        var cleaned = new string([
            .. (value ?? string.Empty)
                .Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)
        ]);

        return cleaned.Replace(" - ", " ").Replace('/', '_').Replace('\\', '_').Trim().Trim('.');
    }

    /// <summary>Trimmed, blanks dropped, duplicates dropped, order preserved. Exactly what was typed.</summary>
    private static List<string> Distinct(IReadOnlyList<string> values)
    {
        var result = new List<string>(values.Count);
        foreach (var value in values)
        {
            var trimmed = value.Trim();
            if (trimmed.Length > 0 && !result.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                result.Add(trimmed);
        }

        return result;
    }
}
