namespace Gaida.Platforms.MusicDatabase;

/// <summary>
///     Where the library lives and where its covers are served from. Each one is optional, as the pod's
///     settings always were: unset means the default below, and only a directory that was set is created at boot.
/// </summary>
/// <param name="Storage">The library root (<c>STORAGE</c>).</param>
/// <param name="AlbumCovers">Where extracted artwork is written (<c>ALBUM_COVERS</c>).</param>
/// <param name="Domain">The public host the covers directory is served under (<c>DOMAIN</c>).</param>
public sealed record LibraryOptions(string? Storage = null, string? AlbumCovers = null, string? Domain = null)
{
    /// <summary>What a stored cover URL carries in place of <see cref="AlbumCoverLocation" />.</summary>
    private const string DomainPlaceholder = "$[DOMAIN]";

    public string StorageDirectory => Storage ?? "./";

    public string CoverDirectory => AlbumCovers ?? "./Album_Covers";

    // TrimEnd because DOMAIN is written with a trailing slash as often as not, and concatenating it
    // straight onto "/Album_Covers" produced "https://host//Album_Covers/<hash>.jpg" — a URL that
    // renders in a browser but that Cover.cs's HttpClient fetch and any strict client both choke on.
    public string AlbumCoverLocation => (Domain ?? string.Empty).TrimEnd('/') + "/Album_Covers";

    /// <summary>
    ///     A cover URL as <c>library.db</c> keeps it: the host is the <c>$[DOMAIN]</c> placeholder, so the rows
    ///     survive <c>DOMAIN</c> changing. <see cref="Served" /> is the way back.
    /// </summary>
    /// <remarks>
    ///     The substitution used to be one-way, which was harmless while only the loader wrote the index — it
    ///     writes entries it has not substituted yet. An admin edit saves an entry that has been, and without
    ///     this it would bake this host's domain into the library's rows. With <c>DOMAIN</c> unset the host is a
    ///     bare "/Album_Covers", which is a prefix of far too much to go substituting blindly.
    /// </remarks>
    public string? Stored(string? coverUrl)
    {
        return coverUrl is null || string.IsNullOrEmpty(Domain)
            ? coverUrl
            : coverUrl.Replace(AlbumCoverLocation, DomainPlaceholder);
    }

    /// <summary>The absolute cover URL everything downstream wants, from what <see cref="Stored" /> wrote.</summary>
    public string? Served(string? storedCoverUrl)
    {
        return storedCoverUrl?.Replace(DomainPlaceholder, AlbumCoverLocation);
    }
}
