using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using SixLabors.ImageSharp;

namespace Snimka;

/// <summary>
///     The library's covers on disk: one file per image, named by the SHA-1 of its bytes. Content addressing is
///     what lets every URL be cached forever and what makes a second upload of the same artwork free.
/// </summary>
/// <param name="root">The directory the originals live in.</param>
public sealed partial class Covers(string root)
{
    public string Root { get; } = root;

    /// <summary>
    ///     Keeps one image and names it. The same name the library pod used to write itself, so every cover
    ///     moved over from the old directory is still where its <c>cover://</c> name says.
    /// </summary>
    /// <returns>The file name (<c>&lt;sha1&gt;.jpg</c> or <c>.png</c>), or <c>null</c> for anything that is not one.</returns>
    [SuppressMessage("Microsoft.Security", "CA5350:DoNotUseWeakCryptographicAlgorithms",
        Justification = "A content address for a file name, not a security boundary, and changing it would " +
                        "rename every stored cover.")]
    public string? Store(byte[] image)
    {
        if (Extension(image) is not { } extension || !Readable(image)) return null;

        var name = $"{Convert.ToHexStringLower(SHA1.HashData(image))}.{extension}";
        var path = Path.Combine(Root, name);
        if (File.Exists(path)) return name;

        // Temp then move, so a reader never sees half a cover. Two uploads of the same bytes racing here
        // both move the same content onto the same name, which is why overwrite is harmless.
        Directory.CreateDirectory(Root);
        var temporary = $"{path}.{Guid.NewGuid():n}.part";
        File.WriteAllBytes(temporary, image);
        File.Move(temporary, path, true);
        return name;
    }

    /// <summary>The file behind a <c>cover://</c> name, or <c>null</c> when it is not one or is not here.</summary>
    public string? Find(string name)
    {
        if (!Name().IsMatch(name)) return null;

        var path = Path.Combine(Root, name);
        return File.Exists(path) ? path : null;
    }

    /// <summary>What <see cref="Store" /> names a cover: a lowercase SHA-1 and the extension, nothing that can climb.</summary>
    [GeneratedRegex("^[0-9a-f]{40}\\.(jpg|png)$")]
    public static partial Regex Name();

    /// <summary>Only a JPEG or a PNG is a cover, which is every cover the library has ever extracted.</summary>
    private static string? Extension(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> png = [137, 80, 78, 71, 13, 10, 26, 10];
        ReadOnlySpan<byte> jpeg = [255, 216, 255];

        if (data.StartsWith(png)) return "png";
        return data.StartsWith(jpeg) ? "jpg" : null;
    }

    /// <summary>
    ///     The right first bytes are not enough: a tag reader that runs past a broken picture frame hands back
    ///     a JPEG marker followed by megabytes of audio. A header ImageSharp can read is what is kept.
    /// </summary>
    internal static bool Readable(byte[] image)
    {
        try
        {
            Image.Identify(image);
            return true;
        }
        catch (ImageFormatException)
        {
            return false;
        }
    }
}
