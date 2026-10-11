using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Gaida.API;

/// <summary>
///     Track artwork kept on disk under <c>THUMBNAIL_CACHE</c>, keyed by a hash of the id it was asked for. The
///     id is a URL of sorts, so it is hashed rather than sanitised: no traversal, no illegal characters, and the
///     extension is the only thing that has to be recovered at serve time.
/// </summary>
public sealed class ThumbnailCache(IOptions<GaidaApiOptions> options, IHttpClientFactory http)
{
    /// <summary>The named client artwork is fetched with.</summary>
    public const string Client = "thumbnails";

    /// <summary>Everything upstream actually serves; anything else is stored and served back as JPEG.</summary>
    private static readonly Dictionary<string, string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg",
        [".png"] = "image/png",
        [".webp"] = "image/webp",
        [".gif"] = "image/gif"
    };

    private string Directory
    {
        get
        {
            var directory = Path.GetFullPath(options.Value.ThumbnailCache is { Length: > 0 } configured
                ? configured
                : Path.Combine(Path.GetTempPath(), "gaida-thumbnails"));
            System.IO.Directory.CreateDirectory(directory);
            return directory;
        }
    }

    /// <returns>The cached file for <paramref name="id" />, or <c>null</c> when it has not been fetched yet.</returns>
    public string? Find(string id)
    {
        return System.IO.Directory.EnumerateFiles(Directory, Key(id) + ".*").FirstOrDefault();
    }

    public static string ContentTypeFor(string path)
    {
        return ImageTypes.GetValueOrDefault(Path.GetExtension(path), "image/jpeg");
    }

    public Task<HttpResponseMessage> FetchAsync(string source, CancellationToken cancellationToken)
    {
        return http.CreateClient(Client).GetAsync(source, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    /// <summary>Serves <paramref name="source" /> into <paramref name="destination" /> and keeps it for next time.</summary>
    /// <remarks>
    ///     ponytail: two requests racing the same cold id both fetch and the last Move wins. Thumbnails are
    ///     tens of kilobytes, so a duplicate download is cheaper than the coordination — add a keyed lock if
    ///     that ever stops being true.
    /// </remarks>
    public Task KeepWhileServingAsync(string id, Stream source, string contentType, Stream destination,
        CancellationToken cancellationToken)
    {
        var directory = Directory;
        var key = Key(id);
        return PumpAsync(source, destination, Path.Combine(directory, $"{key}.{Guid.NewGuid():N}.part"),
            Path.Combine(directory, key + ExtensionFor(contentType)), cancellationToken);
    }

    /// <summary>
    ///     Copies <paramref name="source" /> into the response and the cache at the same time, so the client starts
    ///     receiving the image on the first chunk off the wire instead of after the whole download. Only a body that
    ///     was read to the end is published to <paramref name="cachePath" /> — a truncated image in the cache would
    ///     be served forever.
    /// </summary>
    public static async Task PumpAsync(Stream source, Stream destination, string temporaryPath, string cachePath,
        CancellationToken cancellationToken = default)
    {
        var buffer = new byte[16 * 1024];
        var complete = false;

        try
        {
            await using (var file = File.Create(temporaryPath))
            {
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    // Response first, and flushed: the point of the endpoint is that the image paints while it downloads.
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    await destination.FlushAsync(cancellationToken);
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }

                complete = true;
            }

            File.Move(temporaryPath, cachePath, true);
        }
        finally
        {
            if (!complete) File.Delete(temporaryPath);
        }
    }

    private static string Key(string id)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
    }

    private static string ExtensionFor(string contentType)
    {
        return ImageTypes.FirstOrDefault(pair => pair.Value == contentType).Key ?? ".jpg";
    }
}
