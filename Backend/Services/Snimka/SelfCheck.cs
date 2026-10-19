using System.Security.Cryptography;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Snimka;

/// <summary>
///     The storage half, with no port bound: what a cover is named, what is refused, and that the same bytes
///     twice are one file. Runs in the image build, so a broken store fails it.
/// </summary>
internal static class SelfCheck
{
    public static bool Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "snimka-check-" + Guid.NewGuid().ToString("n"));
        var failures = 0;

        void Assert(bool condition, string what)
        {
            Console.WriteLine($"{(condition ? "ok  " : "FAIL")} {what}");
            if (!condition) failures++;
        }

        try
        {
            var covers = new Covers(root);
            var jpeg = Encode(image => image.SaveAsJpeg);
            var png = Encode(image => image.SaveAsPng);

#pragma warning disable CA5350 // the name under test is a SHA-1, as it has always been
            var expected = Convert.ToHexStringLower(SHA1.HashData(jpeg)) + ".jpg";
#pragma warning restore CA5350

            Assert(covers.Store(jpeg) == expected, "a JPEG is named by the SHA-1 of its bytes, as gaida-local named it");
            Assert(covers.Store(png)?.EndsWith(".png", StringComparison.Ordinal) == true, "a PNG keeps .png");
            Assert(covers.Store(jpeg) == expected && Directory.GetFiles(root).Length == 2,
                "the same bytes twice are one file");
            Assert(covers.Store("not a cover"u8.ToArray()) is null, "something that is no image is refused");
            Assert(covers.Store([255, 216, 255, 0, 1, 2, 3]) is null,
                "a JPEG marker followed by noise is refused, not kept as a cover");
            Assert(covers.Store([]) is null, "an empty body is refused");
            Assert(Directory.GetFiles(root, "*.part").Length == 0, "no temporary file is left behind");

            Assert(covers.Find(expected) is not null, "a stored cover is found by its name");
            Assert(covers.Find("../" + expected) is null && covers.Find(expected.ToUpperInvariant()) is null,
                "a name that is not exactly what Store makes finds nothing");

            ParseCheck(Assert, expected);
            SizeCheck(Assert);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { /* temp dir */ }
        }

        Console.WriteLine(failures == 0 ? "self-check passed" : $"self-check: {failures} failure(s)");
        return failures == 0;
    }

    // What a thumbnailUrl may name. The remote half fetches whatever a stranger's query string says, so what it
    // refuses matters as much as what it takes.
    private static void ParseCheck(Action<bool, string> assert, string name)
    {
        var images = new Images(new Covers("unused"), null!, ["ytimg.com", "dzcdn.net"]);

        assert(images.Parse($"cover://{name}")?.Cover == name, "cover:// is a library cover");
        assert(images.Parse("cover://../../etc/passwd") is null, "cover:// takes a stored name and nothing else");
        assert(images.Parse($"https://gergov.bg/Album_Covers/{name}")?.Cover == name,
            "an old /Album_Covers URL is the same library cover, whatever host it was on");
        assert(images.Parse("https://i.ytimg.com/vi/AbC/hqdefault.jpg")?.Remote is not null,
            "a subdomain of an allowed host is fetched");
        assert(images.Parse("https://cdn-images.dzcdn.net/images/cover/x/1000x1000.jpg")?.Remote is not null,
            "Deezer's CDN is fetched");
        assert(images.Parse("http://i.ytimg.com/vi/AbC/hqdefault.jpg") is null, "plain http is not fetched");
        assert(images.Parse("https://evilytimg.com/x.jpg") is null, "a host merely ending in an allowed name is not");
        assert(images.Parse("https://dom:8080/Audio/Accounts") is null && images.Parse("https://127.0.0.1/x") is null,
            "nothing inside the network is reachable through it");
        assert(images.Parse("/Audio/Playlists/p_1/Cover") is null && images.Parse("") is null,
            "a relative path or nothing is refused");
    }

    private static void SizeCheck(Action<bool, string> assert)
    {
        assert(SizeParser.Snap("100") == "128" && SizeParser.Snap("160") == "160",
            "a size rounds up to a multiple of 32");
        assert(SizeParser.Snap("1") == "32" && SizeParser.Snap("99999") == "1024", "and stays between 32 and 1024");
        assert(SizeParser.Snap("0") is null && SizeParser.Snap("-5") is null && SizeParser.Snap("big") is null &&
               SizeParser.Snap(null) is null, "anything that is not a positive number is no size: the original");
    }

    private static byte[] Encode(Func<Image, Action<Stream>> save)
    {
        using var image = new Image<Rgb24>(4, 4);
        using var stream = new MemoryStream();
        save(image)(stream);
        return stream.ToArray();
    }
}
