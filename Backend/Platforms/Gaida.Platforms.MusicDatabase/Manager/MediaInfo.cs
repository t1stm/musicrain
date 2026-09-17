using System.Diagnostics;
using System.Text.Json;
using Gaida.Core.Utils;

namespace Gaida.Platforms.MusicDatabase.Manager;

public static class MediaInfo
{
    /// <summary>What repeated tag values are joined with: the separator matching already splits on.</summary>
    private const string ArtistSeparator = ", ";

    public static async Task<MusicInfo> GetInformation(string location)
    {
        var musicInfo = new MusicInfo
        {
            Id = string.Empty
        };
        var processedLocation = location.Replace("\"", "\\\"");

        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "ffprobe",
            Arguments = $"-v quiet -of json -show_entries format \"{processedLocation}\"",
            RedirectStandardOutput = true
        });

        if (process == null) return musicInfo;

        // Drain the pipe before waiting on the process, never after: a 64KB buffer against ffprobe's 80KB
        // of output (one .mp3 here carries a 65KB TRAKTOR4 tag) deadlocks the child on write and this on exit.
        JsonDocument json;
        try
        {
            json = await JsonDocument.ParseAsync(process.StandardOutput.BaseStream);
        }
        catch (JsonException)
        {
            return musicInfo;
        }
        finally
        {
            await process.WaitForExitAsync();
        }

        if (!json.RootElement.TryGetProperty("format", out var format)) return musicInfo;

        if (format.TryGetProperty("duration", out var durationString) &&
            double.TryParse(durationString.GetString(), out var length))
            musicInfo.Length = (ulong)(length * 1000);

        if (!format.TryGetProperty("tags", out var tags)) return musicInfo;

        musicInfo.Titles = MusicInfo.Variants(Tag(tags, "TITLE"));
        musicInfo.Artists = MusicInfo.Variants(Merge(Tag(tags, "ARTISTS")), Merge(Tag(tags, "ARTIST")));
        musicInfo.Album = Tag(tags, "ALBUM")?.Trim() is { Length: > 0 } album ? album : null;

        return musicInfo;
    }

    /// <summary>
    ///     A file may carry the same tag more than once — a FLAC with an ARTISTS comment per performer is the
    ///     common case — and ffprobe hands those back as one string joined with ";". Rejoined on the library
    ///     separator so every name survives and <see cref="TitleNormalizer.SplitArtists" /> can split them.
    /// </summary>
    public static string? Merge(string? value)
    {
        return value?.Contains(';') != true
            ? value
            : string.Join(ArtistSeparator,
                value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    ///     ffprobe lowercases ID3v2 keys and passes Vorbis comments and APEv2 through verbatim, so the same
    ///     tag arrives as <c>artist</c> from an .mp3 and <c>ARTIST</c> from a .wv. Matching case-sensitively
    ///     read the tags of one file in six.
    /// </summary>
    private static string? Tag(JsonElement tags, params string[] names)
    {
        foreach (var name in names)
        foreach (var tag in tags.EnumerateObject())
            if (string.Equals(tag.Name, name, StringComparison.OrdinalIgnoreCase))
                return tag.Value.GetString();

        return null;
    }
}
