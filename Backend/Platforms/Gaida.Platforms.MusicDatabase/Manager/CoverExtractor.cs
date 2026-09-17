using System.Security.Cryptography;
using System.Text.Json;

namespace Gaida.Platforms.MusicDatabase.Manager;

public class CoverExtractor
{
    private static readonly Lock ExportLock = new();
    private string _exportLocation = "./Album_Covers";

    public void Extract(string location)
    {
        _exportLocation = Environment.GetEnvironmentVariable("ALBUM_COVERS", EnvironmentVariableTarget.Process) ??
                         _exportLocation;

        Directory.CreateDirectory(_exportLocation);
        Parallel.ForEach(
            Directory.GetDirectories(location, "*", SearchOption.AllDirectories)
                .Where(folder => File.Exists($"{folder}/Info.json")),
            ParseFolder);
    }

    private void ParseFolder(string folder)
    {
        using var fileStream = File.Open($"{folder}/Info.json", FileMode.Open, FileAccess.ReadWrite,
            FileShare.ReadWrite);

        if (fileStream.Length == 0) return;

        var items = JsonSerializer.Deserialize<List<MusicInfo>>(fileStream, MusicInfo.SerializerOptions) ?? [];
        var change = false;

        foreach (var info in items.Where(m => string.IsNullOrWhiteSpace(m.CoverUrl)))
        {
            if (ExportCover(info.ToMusicResult([]).Path) is not { } cover) continue;

            // The placeholder form, because this writes the file itself rather than going through the
            // load that substitutes it — see MusicInfo.StoredCoverUrl.
            info.CoverUrl = $"$[DOMAIN]/{cover}";
            change = true;
        }

        if (!change) return;

        fileStream.SetLength(0);
        fileStream.Position = 0;
        JsonSerializer.Serialize(fileStream, items, MusicInfo.SerializerOptions);
    }

    /// <summary>
    ///     Writes the embedded cover of one audio file into the export directory, deduplicated by content.
    /// </summary>
    /// <remarks>
    ///     Pulled out of <see cref="ParseFolder" /> so the admin import path can extract a cover for the one
    ///     file it just wrote, instead of waiting for the next full <see cref="Extract" /> at startup. The
    ///     caller decides what URL to record: the folder pass writes the <c>$[DOMAIN]</c> placeholder straight
    ///     to disk, while an import is updating an entry already in memory and needs the substituted form.
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
