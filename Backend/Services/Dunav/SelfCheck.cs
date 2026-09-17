using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Gaida.Admin;
using Gaida.Core.Streams;
using Serilog;

namespace Dunav;

/// <summary>
///     The two pieces of Dunav that are worth proving runnable: request coalescing, and the follower read
///     that lets many clients stream one still-downloading body. Run both with
///     <c>dotnet run --project Dunav -- --self-check</c>.
/// </summary>
internal static class SelfCheck
{
    public static async Task<bool> RunAsync()
    {
        return await CoalescingAsync() & await FollowAsync() & AdminRing();
    }

    /// <summary>
    ///     The one thing in <see cref="AdminFeed" /> that can silently go wrong: the ring has to wrap in
    ///     place rather than grow, or an unwatched service leaks a request log forever. Capacity is private,
    ///     so this asserts the observable consequence — a flood leaves a bounded window holding the newest
    ///     entries, not the oldest.
    /// </summary>
    private static bool AdminRing()
    {
        var feed = new AdminFeed();
        const int flood = 5000;

        for (var i = 0; i < flood; i++)
            feed.Record(new RequestEntry(DateTimeOffset.UtcNow, "GET", $"/probe/{i}", 200, i));

        var recent = feed.Recent();
        var ok = recent.Length < flood
                 && recent[^1].Path == $"/probe/{flood - 1}"
                 && recent[0].Path == $"/probe/{flood - recent.Length}";

        Console.WriteLine(ok
            ? $"OK: {flood} requests recorded -> ring holds the newest {recent.Length}"
            : $"FAIL: {flood} requests recorded -> ring holds {recent.Length}, " +
              $"oldest {recent.FirstOrDefault()?.Path}, newest {recent.LastOrDefault()?.Path}");

        return ok;
    }

    /// <summary>
    ///     Smallest runnable proof that <see cref="CacheService.GetOrStartAsync" /> coalesces: N concurrent
    ///     racers for the same key must trigger exactly one upstream HTTP call.
    /// </summary>
    private static async Task<bool> CoalescingAsync()
    {
        var fetchCount = 0;
        var handler = new CountingHandler(() => Interlocked.Increment(ref fetchCount));
        using var http = new HttpClient(handler);
        http.BaseAddress = new Uri("http://unit-test.local");
        using var scratch = new ScratchDir();
        var cache = new CacheService(http, Log.Logger, scratch.Configuration);

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => cache.GetOrStartAsync("check-key",
            entry => cache.FetchAsync(entry, "/probe", CancellationToken.None), out _)));

        var ok = fetchCount == 1
                 && results.All(r => r is not null)
                 && results.Select(r => r!.Body.Path).Distinct().Count() == 1;

        Console.WriteLine(ok
            ? $"OK: {results.Length} concurrent requests -> {fetchCount} upstream fetch(es)"
            : $"FAIL: {results.Length} concurrent requests -> {fetchCount} upstream fetch(es), " +
              $"{results.Count(r => r is null)} null result(s)");

        return ok;
    }

    /// <summary>
    ///     Proof that a <see cref="StreamSpreader" /> can be followed while it is still being written: five
    ///     readers join at five different points -- one cold, three mid-stream, one after the writer has
    ///     closed -- and every one of them must reconstruct the payload exactly.
    /// </summary>
    /// <remarks>
    ///     The primitive lives in Gaida.Core and is covered by Gaida.Tests, but that suite is not in the
    ///     deployed image and this is: a pod that cannot fan out a download is a pod that cannot serve, so
    ///     it is worth proving in place. <c>scripts/FollowProbe</c> narrates the same loop live.
    /// </remarks>
    private static async Task<bool> FollowAsync()
    {
        const int chunk = 64 * 1024;
        using var scratch = new ScratchDir();

        var payload = RandomNumberGenerator.GetBytes(20 * chunk);
        var expected = Convert.ToHexStringLower(SHA256.HashData(payload));
        await using var body = new StreamSpreader(Path.Combine(scratch.Path, "follow-check"), false);

        // ReSharper disable AccessToDisposedClosure -- the finally below drains the writer and the reader
        // fan-out is awaited, so every closure is finished before the `using` disposes `body`.
        var writer = Task.Run(async () =>
        {
            try
            {
                for (var offset = 0; offset < payload.Length; offset += chunk)
                {
                    await body.WriteAsync(payload.AsMemory(offset, Math.Min(chunk, payload.Length - offset)));
                    await Task.Delay(20);
                }
            }
            finally
            {
                await body.CloseAsync();
            }
        });

        int[] joinAtMs = [0, 60, 150, 300, 600];
        string[] hashes;

        try
        {
            // Bounded, so a writer that faults before creating the file reports a failure instead of hanging
            // the whole self-check -- which is exactly what an earlier version of this did.
            for (var waited = 0; !File.Exists(body.Path); waited += 2)
            {
                if (waited > 5000)
                {
                    Console.WriteLine("FAIL: the writer never created the body file");
                    return false;
                }

                await Task.Delay(2);
            }

            hashes = await Task.WhenAll(joinAtMs.Select(async delay =>
            {
                await Task.Delay(delay);
                await using var reader = body.OpenRead();
                using var sink = new MemoryStream(payload.Length);
                await reader.CopyToAsync(sink);
                return Convert.ToHexStringLower(SHA256.HashData(sink.ToArray()));
            }));
        }
        finally
        {
            // The writer holds `body`. Drain it on every path, or a reader that faults leaves it writing
            // into a spreader the `using` has already disposed.
            await writer;
        }
        // ReSharper restore AccessToDisposedClosure

        var matched = hashes.Count(h => h == expected);
        var ok = matched == joinAtMs.Length;

        Console.WriteLine(ok
            ? $"OK: {joinAtMs.Length} readers followed a growing body to a byte-exact copy"
            : $"FAIL: only {matched} of {joinAtMs.Length} readers reconstructed the body");

        return ok;
    }

    /// <summary>A throwaway cache directory, so a self-check run never touches a real one.</summary>
    private sealed class ScratchDir : IDisposable
    {
        public ScratchDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dunav-selfcheck-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(Path);
            Configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Dunav:CacheDir"] = Path })
                .Build();
        }

        public string Path { get; }
        public IConfiguration Configuration { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch (IOException)
            {
                // Best effort -- it is a temp directory.
            }
        }
    }

    private sealed class CountingHandler(Action onSend) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            onSend();

            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/ogg");
            return Task.FromResult(response);
        }
    }
}
