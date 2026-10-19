using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using SixLabors.ImageSharp;

namespace Snimka;

/// <summary>
///     Artwork that lives on another host — YouTube's, Deezer's, Spotify's — fetched the first time anyone asks
///     for it and kept, so every later request is served from disk like a library cover.
/// </summary>
/// <param name="root">Where the fetched images are kept, one file per URL.</param>
/// <param name="http">The <c>remote</c> client: short timeout, and no redirects.</param>
public sealed class Remote(string root, IHttpClientFactory http, Serilog.ILogger logger)
{
    public const string Client = "remote";

    /// <summary>Thumbnails are tens of kilobytes. Anything this big is not one.</summary>
    private const int MaxBytes = 10 * 1024 * 1024;

    /// <summary>A URL that failed is not asked for again for this long: a deleted video's thumbnail stays gone.</summary>
    private static readonly TimeSpan MissFor = TimeSpan.FromMinutes(10);

    private static readonly Dictionary<string, string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["JPEG"] = "jpg", ["PNG"] = "png", ["WEBP"] = "webp", ["GIF"] = "gif"
    };

    /// <summary>One fetch per URL however many requests race for it, as Dunav does for audio.</summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _fetching = new();

    private readonly ConcurrentDictionary<string, DateTimeOffset> _missing = new();

    public string Root { get; } = root;

    public int Missing => _missing.Count(entry => entry.Value > DateTimeOffset.UtcNow);

    /// <returns>The kept file, fetching it first if this is the first time; <c>null</c> when it cannot be had.</returns>
    public async Task<string?> FindAsync(Uri url, CancellationToken cancellationToken)
    {
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url.AbsoluteUri)));
        if (Kept(key) is { } path) return path;
        if (_missing.TryGetValue(key, out var until) && until > DateTimeOffset.UtcNow) return null;

        // Not the caller's token: a listener scrolling past must not cancel the fetch for the next one.
        var fetch = _fetching.GetOrAdd(key, _ => new Lazy<Task<string?>>(() => FetchAsync(url, key))).Value;
        return await fetch.WaitAsync(cancellationToken);
    }

    public static string? ContentType(string path)
    {
        return Path.GetExtension(path).ToUpperInvariant() switch
        {
            ".JPG" => "image/jpeg",
            ".PNG" => "image/png",
            ".WEBP" => "image/webp",
            ".GIF" => "image/gif",
            _ => null
        };
    }

    private string? Kept(string key)
    {
        return Extensions.Values.Select(extension => Path.Combine(Root, $"{key}.{extension}")).FirstOrDefault(File.Exists);
    }

    private async Task<string?> FetchAsync(Uri url, string key)
    {
        try
        {
            using var response = await http.CreateClient(Client).GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxBytes)
                return Miss(key, url, $"{(int)response.StatusCode}");

            await using var body = await response.Content.ReadAsStreamAsync();
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await body.ReadAsync(chunk)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxBytes) return Miss(key, url, "too large");
            }

            var image = buffer.ToArray();
            if (Format(image) is not { } extension || !Covers.Readable(image)) return Miss(key, url, "not an image");

            Directory.CreateDirectory(Root);
            var path = Path.Combine(Root, $"{key}.{extension}");
            var temporary = $"{path}.{Guid.NewGuid():n}.part";
            await File.WriteAllBytesAsync(temporary, image);
            File.Move(temporary, path, true);
            return path;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return Miss(key, url, exception.Message);
        }
        finally
        {
            _fetching.TryRemove(key, out _);
        }
    }

    private string? Miss(string key, Uri url, string why)
    {
        logger.Debug("Could not keep {Url}: {Why}", url, why);
        _missing[key] = DateTimeOffset.UtcNow + MissFor;
        return null;
    }

    private static string? Format(byte[] image)
    {
        try
        {
            return Extensions.GetValueOrDefault(Image.DetectFormat(image).Name);
        }
        catch (ImageFormatException)
        {
            return null;
        }
    }
}
