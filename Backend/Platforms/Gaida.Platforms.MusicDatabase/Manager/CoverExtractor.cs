using System.Security.Cryptography;
using ATL;
using Serilog;

namespace Gaida.Platforms.MusicDatabase.Manager;

/// <param name="logger">Where unreadable files are reported.</param>
/// <param name="exportLocation">The covers directory, <c>ALBUM_COVERS</c>.</param>
public class CoverExtractor(ILogger logger, string exportLocation)
{
    private static readonly Lock ExportLock = new();

    /// <summary>
    ///     Writes the embedded cover of one audio file into the export directory, deduplicated by content.
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
        // same 13-byte APIC (Оркестър Колорит - Миленово хоро.mp3) into 12 MB of the audio after it.
        return image is not null && ImageFiletype(image).Length > 0 ? StoreCover(image) : null;
    }

    /// <summary>
    ///     Writes one cover image into the export directory, named by its own content hash.
    /// </summary>
    /// <remarks>
    ///     Separate from <see cref="ExportCover" /> for the import path, whose artwork comes off the source's
    ///     API rather than out of the file — a Deezer FLAC usually carries no embedded picture. Content
    ///     addressing is what makes that safe: an album imported twice writes one file.
    /// </remarks>
    /// <returns>The cover's file name (<c>&lt;hash&gt;.jpg</c>).</returns>
    public string StoreCover(byte[] image)
    {
        var name = $"{Convert.ToHexStringLower(SHA1.HashData(image))}.{ImageFiletype(image)}";
        var filename = $"{exportLocation}/{name}";

        // ponytail: one lock for every cover write; they are rare and small, split it per-hash if that ever shows up.
        lock (ExportLock)
        {
            Directory.CreateDirectory(exportLocation);
            if (!File.Exists(filename)) File.WriteAllBytes(filename, image);
        }

        return name;
    }

    private static string ImageFiletype(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> pngHeader = [137, 80, 78, 71, 13, 10, 26, 10];
        ReadOnlySpan<byte> jpegHeader = [255, 216, 255];

        if (data.StartsWith(pngHeader)) return "png";
        return data.StartsWith(jpegHeader) ? "jpg" : "";
    }
}
