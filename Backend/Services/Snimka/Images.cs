using System.Text.RegularExpressions;

namespace Snimka;

/// <summary>
///     What a track's <c>thumbnailUrl</c> points at, and the file on disk behind it. Every image the frontend shows
///     for a track comes through here, whoever it came from.
/// </summary>
/// <param name="hosts">
///     The hosts a remote image may come from (<c>SNIMKA_HOSTS</c>), each one also standing for its subdomains.
///     Anything else is refused: this fetches what a stranger's query string names, and must not fetch from
///     inside the network it runs on.
/// </param>
public sealed partial class Images(Covers covers, Remote remote, IReadOnlyList<string> hosts)
{
    /// <summary>The one public route, mapped on the full path nginx proxies through untouched.</summary>
    public const string Route = "/Audio/Cover/Get";

    private const string CoverScheme = "cover://";

    /// <summary>A library cover, or an image elsewhere — exactly one of the two.</summary>
    public sealed record Source(string? Cover, Uri? Remote);

    public Source? Parse(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        if (url.StartsWith(CoverScheme, StringComparison.Ordinal))
            return Covers.Name().IsMatch(url.AsSpan(CoverScheme.Length)) ? new Source(url[CoverScheme.Length..], null) : null;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) return null;

        // How a cover was named before cover://, as a file nginx served off the host: the library's index wrote
        // these until now, and every playlist track Dom saved still carries one. Same name, same file.
        if (LegacyCover().Match(uri.AbsolutePath) is { Success: true } legacy) return new Source(legacy.Groups[1].Value, null);

        return uri.Scheme == "https" && Allowed(uri.Host) ? new Source(null, uri) : null;
    }

    /// <returns>The file to serve or resize, or <c>null</c> when the URL is refused or the image cannot be had.</returns>
    public async Task<string?> ResolveAsync(string? url, CancellationToken cancellationToken)
    {
        return Parse(url) switch
        {
            { Cover: { } name } => covers.Find(name),
            { Remote: { } uri } => await remote.FindAsync(uri, cancellationToken),
            _ => null
        };
    }

    private bool Allowed(string host)
    {
        return hosts.Any(allowed => host.Equals(allowed, StringComparison.OrdinalIgnoreCase) ||
                                    host.EndsWith("." + allowed, StringComparison.OrdinalIgnoreCase));
    }

    [GeneratedRegex("^/Album_Covers/([0-9a-f]{40}\\.(?:jpg|png))$")]
    private static partial Regex LegacyCover();
}
