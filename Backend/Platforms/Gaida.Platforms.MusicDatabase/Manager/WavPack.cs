namespace Gaida.Platforms.MusicDatabase.Manager;

public static class WavPack
{
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
