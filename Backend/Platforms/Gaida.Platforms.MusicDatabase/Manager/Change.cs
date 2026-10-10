using System.Diagnostics.CodeAnalysis;

namespace Gaida.Platforms.MusicDatabase.Manager;

/// <summary>An edit, import or lyrics stamp: the entry as it now is, or why it was refused.</summary>
/// <param name="UnknownSong">The refusal was that no song has the ID — a 404 rather than a 400.</param>
public readonly record struct Change(MusicInfo? Entry, string? Error, bool UnknownSong = false)
{
    /// <summary>No song has the ID the change named.</summary>
    public static readonly Change NoSuchSong = new(null, "No song with that ID.", true);

    [MemberNotNullWhen(true, nameof(Entry))]
    [MemberNotNullWhen(false, nameof(Error))]
    public bool Ok => Error is null;

    public static Change Refused(string error)
    {
        return new Change(null, error);
    }

    public static implicit operator Change(MusicInfo entry)
    {
        return new Change(entry, null);
    }
}
