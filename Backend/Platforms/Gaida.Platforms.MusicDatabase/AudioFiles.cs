namespace Gaida.Platforms.MusicDatabase;

/// <summary>
///     The files the library indexes, and the content type each is served as. Gaida and Dunav relay the type
///     verbatim, so a wrong one here breaks playback downstream.
/// </summary>
public static class AudioFiles
{
    private static readonly (string Extension, string ContentType)[] Formats =
    [
        (".flac", "audio/flac"),
        (".ogg", "audio/ogg"),
        (".mp3", "audio/mpeg"),
        (".wav", "audio/wav"),
        (".mka", "audio/mka"),
        (".adts", "audio/aac"),
        (".wma", "audio/x-ms-wma"),
        (".wv", "audio/x-wavpack")
    ];

    /// <summary>Whether the library indexes a file by this name. Case-sensitive, as the scan always was.</summary>
    public static bool IsAudio(ReadOnlySpan<char> fileName)
    {
        foreach (var (extension, _) in Formats)
            if (fileName.EndsWith(extension, StringComparison.Ordinal))
                return true;

        return false;
    }

    public static string ContentTypeFor(string extension)
    {
        var lower = extension.ToLowerInvariant();
        foreach (var format in Formats)
            if (format.Extension == lower)
                return format.ContentType;

        return "application/octet-stream";
    }
}
