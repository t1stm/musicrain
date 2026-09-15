using TagLib;
using File = System.IO.File;

namespace Gaida.Platforms.MusicDatabase.Manager;

public static class WavPack
{
    /// <returns>The embedded cover, or <c>null</c> when the file has none.</returns>
    public static byte[]? GetImageFromFile(string location)
    {
        if (!File.Exists(location) || !location.EndsWith(".wv")) return null;

        // ponytail: taglib is already referenced and reads the APEv2 "Cover Art (Front)" item, so no wvunpack subprocess.
        var pictures = TagLib.File.Create(location).GetTag(TagTypes.Ape)?.Pictures;
        return pictures is null || pictures.Length < 1 ? null : pictures[0].Data.Data;
    }

    /// <returns>
    ///     The correction file beside <paramref name="location" />, or <c>null</c> when the track is not
    ///     hybrid. Its presence is the only signal that ffmpeg alone would decode the file lossily: the
    ///     .wv holds a lossy core, the .wvc holds what makes the pair bit-exact, and ffmpeg's WavPack
    ///     decoder reads only the former.
    /// </returns>
    /// <remarks>
    ///     WavPack names the correction file <c>song.wvc</c> — the audio path with a <c>c</c> appended,
    ///     not a replaced extension, so <see cref="Path.ChangeExtension" /> is the wrong tool here.
    /// </remarks>
    public static string? CorrectionFor(string location)
    {
        if (!location.EndsWith(".wv", StringComparison.OrdinalIgnoreCase)) return null;

        var correction = location + "c";
        return File.Exists(correction) ? correction : null;
    }
}
