using System.Security.Cryptography;

namespace Gaida.Platforms.MusicDatabase.Manager;

public class CoverExtractor
{
    private static readonly Lock ExportLock = new();
    private string _exportLocation = "./Album_Covers";

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
        _exportLocation = Environment.GetEnvironmentVariable("ALBUM_COVERS", EnvironmentVariableTarget.Process) ??
                         _exportLocation;

        // A file that has been deleted since the last scan is not an error worth a crash: Flac and
        // WavPack answer null for a missing path, but Id3V2 throws, and that took the whole library
        // load down with it.
        if (!File.Exists(location)) return null;

        byte[]? image;
        try
        {
            image = Flac.GetImageFromFile(location) ?? WavPack.GetImageFromFile(location) ??
                Id3V2.GetImageFromTag(location);
        }
        catch (Exception)
        {
            // Same rule as the missing file above, for a file that is there but unreadable: TagLib throws
            // CorruptFileException on a truncated or mislabelled .mp3, and this runs inside the
            // Parallel.ForEach of the library scan — one bad file must not take the whole load with it.
            return null;
        }

        return image is null ? null : StoreCover(image);
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
        _exportLocation = Environment.GetEnvironmentVariable("ALBUM_COVERS", EnvironmentVariableTarget.Process) ??
                         _exportLocation;

        var name = $"{Convert.ToHexStringLower(SHA1.HashData(image))}.{Flac.GetImageFiletype(image)}";
        var filename = $"{_exportLocation}/{name}";

        // ponytail: one lock for every cover write; they are rare and small, split it per-hash if that ever shows up.
        lock (ExportLock)
        {
            Directory.CreateDirectory(_exportLocation);
            if (!File.Exists(filename)) File.WriteAllBytes(filename, image);
        }

        return name;
    }
}
