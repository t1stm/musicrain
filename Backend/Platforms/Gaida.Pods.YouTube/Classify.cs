using System.Text.RegularExpressions;

namespace Gaida.Pods.YouTube;

/// <summary>
///     Outcome of classifying one raw query. <see cref="Status" /> is the HTTP status <c>/classify</c> answers with:
///     200 (recognised, <see cref="Kind" />/<see cref="Id" /> set), 400 (recognisably ours but malformed,
///     <see cref="Error" /> set) or 404 (not ours at all -- Gaida.API defaults that to a keyword search).
/// </summary>
public readonly record struct ClassifyResult(int Status, string? Kind, string? Id, string? Error);

/// <summary>
///     The YouTube half of what used to be Gaida.API's <c>QueryParser</c> (see
///     <c>Gaida.API/Contracts/QueryParser.cs</c>), scoped to this platform's own identifiers.
///     Recognises <c>yt://</c> / <c>yt-playlist://</c> ids, YouTube URLs (watch/youtu.be/shorts/embed/live/playlist),
///     a bare 11-character video ID and a bare playlist ID (<c>PL</c>/<c>UU</c>/<c>LL</c>/<c>RD</c>/<c>FL</c>/<c>WL</c>/
///     <c>OLAK5uy_</c> prefixed). Pure string parsing -- no network, no platform instance needed.
/// </summary>
public static partial class Classify
{
    private static readonly ClassifyResult NotMine = new(404, null, null, null);

    public static ClassifyResult Parse(string? value)
    {
        var query = value?.Trim();
        if (string.IsNullOrEmpty(query)) return NotMine;

        if (query.StartsWith("yt://", StringComparison.OrdinalIgnoreCase))
            return ParseVideoId(query["yt://".Length..]);

        if (query.StartsWith("yt-playlist://", StringComparison.OrdinalIgnoreCase))
            return ParsePlaylistId(query["yt-playlist://".Length..]);

        if (Uri.TryCreate(query, UriKind.Absolute, out var uri))
            return ParseUrl(uri);

        if (VideoIdRegex().IsMatch(query))
            return ParseVideoId(query);

        if (LooksLikePlaylistId(query))
            return ParsePlaylistId(query);

        // Schemeless playlist link (e.g. "youtube.com/playlist?list=PL...") that Uri.TryCreate rejected for
        // lacking a scheme. Same shape YouTube.cs:72-75 (IsPlaylistUrl) matches against arbitrary text.
        var schemeless = SchemelessPlaylistRegex().Match(query);
        return schemeless.Success ? ParsePlaylistId(schemeless.Groups[1].Value) : NotMine;
    }

    private static ClassifyResult ParseUrl(Uri uri)
    {
        if (!IsYouTubeHost(uri.Host)) return NotMine;

        var parameters = ParseQuery(uri.Query);
        if (parameters.TryGetValue("list", out var playlistId))
            return ParsePlaylistId(playlistId);

        var pathParts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var videoId = uri.Host.EndsWith("youtu.be", StringComparison.OrdinalIgnoreCase)
            ? pathParts.FirstOrDefault()
            : uri.AbsolutePath.Equals("/watch", StringComparison.OrdinalIgnoreCase)
                ? parameters.GetValueOrDefault("v")
                : pathParts is ["shorts" or "embed" or "live", _, ..]
                    ? pathParts[1]
                    : null;

        return videoId is null
            ? Invalid("The YouTube URL does not contain a video or playlist ID.")
            : ParseVideoId(videoId);
    }

    private static ClassifyResult ParseVideoId(string id)
    {
        return VideoIdRegex().IsMatch(id)
            ? Ok("id", "yt://" + id)
            : Invalid("The YouTube video ID is invalid.");
    }

    private static ClassifyResult ParsePlaylistId(string id)
    {
        return PlaylistIdRegex().IsMatch(id)
            ? Ok("playlist", "yt-playlist://" + id)
            : Invalid("The YouTube playlist ID is invalid.");
    }

    private static bool IsYouTubeHost(string host)
    {
        return host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".youtu.be", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikePlaylistId(string value)
    {
        return value.Length is >= 10 and <= 128 && PlaylistIdRegex().IsMatch(value) &&
               (value.StartsWith("PL", StringComparison.Ordinal) || value.StartsWith("UU", StringComparison.Ordinal) ||
                value.StartsWith("LL", StringComparison.Ordinal) || value.StartsWith("RD", StringComparison.Ordinal) ||
                value.StartsWith("FL", StringComparison.Ordinal) || value.StartsWith("WL", StringComparison.Ordinal) ||
                value.StartsWith("OLAK5uy_", StringComparison.Ordinal));
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        return query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .GroupBy(parts => Uri.UnescapeDataString(parts[0]), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => Uri.UnescapeDataString(group.First()[1]),
                StringComparer.OrdinalIgnoreCase);
    }

    private static ClassifyResult Ok(string kind, string id)
    {
        return new ClassifyResult(200, kind, id, null);
    }

    private static ClassifyResult Invalid(string message)
    {
        return new ClassifyResult(400, null, null, message);
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{11}$")]
    private static partial Regex VideoIdRegex();

    [GeneratedRegex("^[A-Za-z0-9_-]{10,128}$")]
    private static partial Regex PlaylistIdRegex();

    [GeneratedRegex(@"/playlist\?list=([a-zA-Z0-9_-]+)")]
    private static partial Regex SchemelessPlaylistRegex();
}