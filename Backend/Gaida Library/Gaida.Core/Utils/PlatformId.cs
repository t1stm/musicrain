using System.Diagnostics.CodeAnalysis;

namespace Gaida.Core.Utils;

/// <summary>A platform ID is a protocol prefix and a value: <c>audio://abc</c>, <c>yt://dQw4w9WgXcQ</c>.</summary>
public static class PlatformId
{
    public const string Separator = "://";

    /// <summary>Splits off the protocol prefix, separator included (<c>yt://</c>). False when there is none.</summary>
    public static bool TrySplit(string id, [NotNullWhen(true)] out string? prefix, [NotNullWhen(true)] out string? value)
    {
        var separator = id.IndexOf(Separator, StringComparison.Ordinal);
        if (separator < 1)
        {
            (prefix, value) = (null, null);
            return false;
        }

        var end = separator + Separator.Length;
        (prefix, value) = (id[..end], id[end..]);
        return true;
    }

    /// <summary>The ID without its protocol prefix, or the ID itself when it has none.</summary>
    public static string StripPrefix(string id)
    {
        var separator = id.IndexOf(Separator, StringComparison.Ordinal);
        return separator < 0 ? id : id[(separator + Separator.Length)..];
    }

    /// <summary>The ID without its protocol prefix, escaped so it is safe to put in a header.</summary>
    public static string FileId(string id)
    {
        return Uri.EscapeDataString(StripPrefix(id));
    }
}
