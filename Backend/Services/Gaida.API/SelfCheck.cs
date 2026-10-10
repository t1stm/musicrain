using Gaida.Core;
using Serilog.Core;

namespace Gaida.API;

/// <summary>
///     The one runnable check: <c>dotnet run --project Gaida.API -- --self-check</c>. Exercises the classify
///     fan-out's fallback rule (nobody claims → keyword search) without a listening host or any pods up.
/// </summary>
internal static class SelfCheck
{
    public static void Run()
    {
        var noPods = new ConfigurationBuilder().Build();
        var factory = new ServiceCollection().AddHttpClient().BuildServiceProvider()
            .GetRequiredService<IHttpClientFactory>();
        var manager = new ManagerService(Logger.None, noPods, factory);
        var claim = manager.ClassifyAsync("some random text").GetAwaiter().GetResult();
        Assert(claim is { Kind: QueryType.Keywords, Error: null, Query: "some random text" },
            "classify: no platform pod claiming the query falls back to a keyword search");

        Console.WriteLine("selftest OK");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"selftest failed: {message}");
    }
}
