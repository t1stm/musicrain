using Gaida.Infrastructure.Platforms;
using Gaida.Platforms.MusicDatabase;
using ILogger = Serilog.ILogger;
using Library = Gaida.Platforms.MusicDatabase.MusicDatabase;

namespace Gaida.Pods.MusicDatabase;

/// <summary>
///     Pulls one Deezer track into the library. The Deezer pod owns the download and its cache; this end owns
///     the storage, so the bytes come over the wire rather than the volume being shared. The entry is left
///     exactly as its tags describe it — the admin Library tab is where an operator fixes it up.
/// </summary>
/// <param name="deezerUrl">
///     The Deezer pod, the library pod's one outbound dependency and only from the admin surface. Unset, an
///     import answers 400 and nothing else here notices.
/// </param>
public sealed class DeezerImport(IHttpClientFactory factory, string? deezerUrl, Library library, ILogger logger)
{
    public const string Client = "deezer";

    /// <returns>The indexed entry, or the reason it was not imported.</returns>
    public async Task<(MusicInfo? Entry, string? Error)> ImportAsync(string? id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id)) return (null, "A Deezer track ID is required.");
        if (string.IsNullOrWhiteSpace(deezerUrl)) return (null, "DEEZER_URL is not configured on this pod.");

        var http = factory.CreateClient(Client);
        var query = $"?id={Uri.EscapeDataString(id)}";

        PodResultDto? metadata;
        try
        {
            metadata = await http.GetFromJsonAsync<PodResultDto>($"{deezerUrl}/resolve{query}", ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.Warning(exception, "Deezer would not describe {ID}", id);
            return (null, "The Deezer pod does not know that track.");
        }

        if (metadata is null) return (null, "The Deezer pod does not know that track.");

        // ResponseHeadersRead, so the body streams into the library file instead of through a byte array
        // the size of a FLAC first.
        using var audio = await http.GetAsync($"{deezerUrl}/content{query}", HttpCompletionOption.ResponseHeadersRead, ct);
        if (!audio.IsSuccessStatusCode)
            return (null, "The Deezer pod has no audio for that track — it needs a working DEEZER_ARL.");

        // The content type, not the filename: the pod sets both, and only one of them is a contract
        // Gaida.API already relies on (Content.cs relays it verbatim).
        var extension = ExtensionFor(audio.Content.Headers.ContentType?.MediaType);
        if (extension is null)
            return (null, $"The Deezer pod sent {audio.Content.Headers.ContentType?.MediaType ?? "no content type"}.");

        // Deezer's own artwork, for the usual case of a download with no embedded picture. Fetched before
        // the body so a cover that will not load costs nothing — a track is worth importing without one.
        var cover = await CoverBytesAsync(http, metadata.ThumbnailUrl, ct);

        await using var body = await audio.Content.ReadAsStreamAsync(ct);
        var (entry, error) = await library.ImportAsync(metadata.Artist ?? "", metadata.Name ?? "", metadata.Album,
            extension, body, cover, ct);

        if (entry is null) return (null, error);

        logger.Information("Imported Deezer track {ID} as {Entry}", id, entry.Id);
        return (entry, null);
    }

    /// <summary>
    ///     The cover image a pod's thumbnailUrl points at, or null for anything that does not fetch. Never
    ///     throws: artwork is the one part of an import worth losing rather than failing over.
    /// </summary>
    private async Task<byte[]?> CoverBytesAsync(HttpClient http, string? url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        try
        {
            var bytes = await http.GetByteArrayAsync(url, ct);
            return bytes.Length > 0 ? bytes : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.Warning(exception, "Could not fetch the cover at {Url}", url);
            return null;
        }
    }

    /// <summary>
    ///     The library extension for what the Deezer pod says it is sending, or null for anything this library
    ///     does not index. Deezer only ever serves these two — see Gaida.Pods.Deezer/stream.py.
    /// </summary>
    private static string? ExtensionFor(string? contentType)
    {
        return contentType switch
        {
            "audio/flac" => ".flac",
            "audio/mpeg" => ".mp3",
            _ => null
        };
    }
}
