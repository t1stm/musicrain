using ATL;
using Serilog;

namespace Gaida.Platforms.MusicDatabase.Manager;

/// <param name="logger">Where unreadable files and refused uploads are reported.</param>
/// <param name="snimka">The cover service, <c>SNIMKA_URL</c>; <c>null</c> keeps no cover at all.</param>
public class CoverExtractor(ILogger logger, HttpClient? snimka)
{
    /// <summary>
    ///     Hands the embedded cover of one audio file to Snimka, which keeps it deduplicated by content.
    /// </summary>
    /// <remarks>
    ///     One file at a time, so the scan's cover pass and the admin import path share it. The caller
    ///     decides what URL to record.
    /// </remarks>
    /// <returns>The cover's file name (<c>&lt;hash&gt;.jpg</c>), or <c>null</c> when the file carries none.</returns>
    public string? ExportCover(string location)
    {
        // A file that has been deleted since the last scan is not an error worth a crash.
        if (!File.Exists(location)) return null;

        byte[]? image;
        try
        {
            // The front cover when the file says which one that is, else the first picture: what metaflac and
            // TagLib# handed back before, for every file in the library that names none.
            var pictures = MediaInfo.Read(location).EmbeddedPictures;
            image = (pictures.FirstOrDefault(picture => picture.PicType == PictureInfo.PIC_TYPE.Front) ??
                     pictures.FirstOrDefault())?.PictureData;
        }
        catch (Exception exception)
        {
            // Same rule as the missing file above, for a file that is there but unreadable: this runs inside
            // the Parallel.ForEach of the library scan, and one bad file must not take the whole load with it.
            logger.Warning(exception, "Could not read the artwork of {Location}", location);
            return null;
        }

        // Only a JPEG or a PNG is a cover, which is every cover in the library. Anything else is a broken
        // picture frame: TagLib# handed one back empty and it became a zero-byte file, and ATL reads past the
        // same 13-byte APIC (Оркестър Колорит - Миленово хоро.mp3) into 12 MB of the audio after it. Checked
        // here as well as in Snimka, so twelve megabytes of audio never cross the network to be refused.
        return image is not null && IsCover(image) ? StoreCover(image) : null;
    }

    /// <summary>
    ///     Uploads one cover image to Snimka, which names it by its own content hash.
    /// </summary>
    /// <remarks>
    ///     Separate from <see cref="ExportCover" /> for the import path, whose artwork comes off the source's
    ///     API rather than out of the file — a Deezer FLAC usually carries no embedded picture. Content
    ///     addressing is what makes that safe: an album imported twice is one file.
    ///     <para>
    ///         Synchronous, because the scan's cover pass is a <c>Parallel.ForEach</c>. Snimka down or refusing
    ///         is a cover not kept, not a failed scan: the entry keeps no cover URL, and the next scan asks again.
    ///     </para>
    /// </remarks>
    /// <returns>The cover's file name (<c>&lt;hash&gt;.jpg</c>), or <c>null</c> when it was not kept.</returns>
    public string? StoreCover(byte[] image)
    {
        if (snimka is null) return null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "store");
            request.Content = new ByteArrayContent(image);
            using var response = snimka.Send(request);
            using var reader = new StreamReader(response.Content.ReadAsStream());
            var body = reader.ReadToEnd();

            if (response.IsSuccessStatusCode) return body;

            logger.Warning("Snimka refused a cover ({Status}): {Body}", (int)response.StatusCode, body);
            return null;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            logger.Warning(exception, "Could not reach Snimka to keep a cover");
            return null;
        }
    }

    private static bool IsCover(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> pngHeader = [137, 80, 78, 71, 13, 10, 26, 10];
        ReadOnlySpan<byte> jpegHeader = [255, 216, 255];

        return data.StartsWith(pngHeader) || data.StartsWith(jpegHeader);
    }
}
