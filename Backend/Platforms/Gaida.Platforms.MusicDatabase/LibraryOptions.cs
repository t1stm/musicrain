namespace Gaida.Platforms.MusicDatabase;

/// <summary>
///     Where the library lives and where its covers are kept. Each one is optional, as the pod's settings always
///     were: unset means the default below, and only a directory that was set is created at boot.
/// </summary>
/// <param name="Storage">The library root (<c>STORAGE</c>).</param>
/// <param name="Snimka">
///     The cover service extracted artwork is uploaded to (<c>SNIMKA_URL</c>). Unset, no cover is kept and every
///     song without one is tried again on the next scan.
/// </param>
public sealed record LibraryOptions(string? Storage = null, HttpClient? Snimka = null)
{
    /// <summary>
    ///     A library cover as every track hands it out: <c>cover://&lt;sha1&gt;.jpg</c>, the name Snimka keeps it
    ///     under. Like <c>audio://</c> for a track, it names the thing rather than a host it happens to be on, so
    ///     nothing in library.db or in a saved playlist moves when a host does.
    /// </summary>
    public const string CoverScheme = "cover://";

    /// <summary>
    ///     What older versions wrote in front of a cover's file name: a placeholder for the host that
    ///     <c>DOMAIN</c> filled in on the way out. Rows still carry it until they are next written.
    /// </summary>
    private const string DomainPlaceholder = "$[DOMAIN]/";

    public string StorageDirectory => Storage ?? "./";

    public static string CoverUrl(string name)
    {
        return CoverScheme + name;
    }

    /// <summary>
    ///     A cover as library.db or an older Info.json holds it, as a <c>cover://</c> URL: the placeholder older
    ///     versions stored, and the absolute <c>…/Album_Covers/</c> URL an admin edit could bake in, both name the
    ///     file last. Anything else is passed through.
    /// </summary>
    public static string? Served(string? stored)
    {
        if (stored is null || !(stored.StartsWith(DomainPlaceholder, StringComparison.Ordinal) ||
                                stored.Contains("/Album_Covers/", StringComparison.Ordinal)))
            return stored;

        return CoverUrl(stored[(stored.LastIndexOf('/') + 1)..]);
    }
}
