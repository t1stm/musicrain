namespace Gaida.Pods.YouTube;

/// <summary>
///     ponytail: the one runnable check for Classify's parsing -- no test project, just asserts that fail loudly.
///     Run with `dotnet run -- --self-check`.
/// </summary>
public static class ClassifySelfCheck
{
    public static void Run()
    {
        Expect(Classify.Parse("yt://dQw4w9WgXcQ"), 200, "id", "yt://dQw4w9WgXcQ", null);
        Expect(Classify.Parse("yt://short"), 400, null, null, "The YouTube video ID is invalid.");
        Expect(Classify.Parse("dQw4w9WgXcQ"), 404, null, null, null);
        Expect(Classify.Parse("Innervision"), 404, null, null, null);
        Expect(Classify.Parse("https://www.youtube.com/watch?v=dQw4w9WgXcQ"), 200, "id", "yt://dQw4w9WgXcQ", null);
        Expect(Classify.Parse("https://youtu.be/dQw4w9WgXcQ"), 200, "id", "yt://dQw4w9WgXcQ", null);
        Expect(Classify.Parse("https://www.youtube.com/playlist?list=PLrAXtmErZgOeiKm4sgNOknGvNjby9efdf"),
            200, "playlist", "yt-playlist://PLrAXtmErZgOeiKm4sgNOknGvNjby9efdf", null);
        Expect(Classify.Parse("yt-playlist://PLrAXtmErZgOeiKm4sgNOknGvNjby9efdf"),
            200, "playlist", "yt-playlist://PLrAXtmErZgOeiKm4sgNOknGvNjby9efdf", null);
        Expect(Classify.Parse("PLrAXtmErZgOeiKm4sgNOknGvNjby9efdf"), 404, null, null, null);
        Expect(Classify.Parse("youtube.com/playlist?list=PLxyz123456"),
            200, "playlist", "yt-playlist://PLxyz123456", null);
        Expect(Classify.Parse("https://example.com/watch?v=dQw4w9WgXcQ"), 404, null, null, null);
        Expect(Classify.Parse("https://www.youtube.com/results?search_query=test"),
            400, null, null, "The YouTube URL does not contain a video or playlist ID.");
        Expect(Classify.Parse("hello world search text"), 404, null, null, null);
        Expect(Classify.Parse(""), 404, null, null, null);
        Expect(Classify.Parse(null), 404, null, null, null);

        Console.WriteLine("ClassifySelfCheck: OK");
    }

    private static void Expect(ClassifyResult actual, int status, string? kind, string? id, string? error)
    {
        if (actual.Status == status && actual.Kind == kind && actual.Id == id && actual.Error == error) return;

        throw new Exception(
            $"ClassifySelfCheck failed: expected ({status},{kind},{id},{error}) got " +
            $"({actual.Status},{actual.Kind},{actual.Id},{actual.Error})");
    }
}