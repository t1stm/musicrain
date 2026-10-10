namespace Gaida.Core.Utils;

public static class FolderPath
{
    /// <summary>A library folder the way every layer compares it: forward slashes, none leading or trailing.</summary>
    public static string Normalize(string? path)
    {
        return (path ?? string.Empty).Replace('\\', '/').Trim('/');
    }
}
