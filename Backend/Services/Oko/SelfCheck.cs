using System.Net;
using System.Text;
using Microsoft.Extensions.Primitives;
using System.Text.Json;
using Serilog.Core;

namespace Oko;

/// <summary>
///     The two pieces of Oko that are worth proving runnable: the door, and the fan-in's behaviour when
///     a service is down. Run with <c>dotnet run --project Oko -- --self-check</c>.
/// </summary>
internal static class SelfCheck
{
    public static async Task<bool> RunAsync()
    {
        return Door() & await FanInAsync() & Audit();
    }

    /// <summary>
    ///     The audit log's one job it can silently get wrong: <c>reset-password</c> carries the new
    ///     password as a parameter, and a log that records it has turned the safety feature into the
    ///     place passwords pile up. Also checks the ring caps rather than growing.
    /// </summary>
    private static bool Audit()
    {
        var described = AuditLog.Describe(new QueryCollection(new Dictionary<string, StringValues>
        {
            ["username"] = "kris",
            ["password"] = "hunter2",
            ["adminToken"] = "s3cret",
            ["isPublic"] = "true"
        }));

        var redacted = !described.Contains("hunter2")
                       && !described.Contains("s3cret")
                       && described.Contains("username=kris")
                       && described.Contains("isPublic=true")
                       && described.Contains("password=***");

        var log = new AuditLog();
        const int flood = 3000;
        for (var i = 0; i < flood; i++)
            log.Record(new AuditEntry(DateTimeOffset.UtcNow, "kris", "dom", "delete-user", $"username=u{i}", 200, null));

        var kept = log.Recent();
        var capped = kept.Length < flood && kept[^1].Parameters == $"username=u{flood - 1}";

        Console.WriteLine(redacted
            ? "OK: secrets are redacted from the audit log, ordinary parameters are kept"
            : $"FAIL: audit log recorded '{described}'");
        Console.WriteLine(capped
            ? $"OK: {flood} audited actions -> log holds the newest {kept.Length}"
            : $"FAIL: {flood} audited actions -> log holds {kept.Length}");

        return redacted && capped;
    }

    /// <summary>
    ///     Basic parsing, including the cases that would quietly let the wrong person in: a right
    ///     password under the wrong username, a password containing the separator, and every shape of
    ///     malformed header.
    /// </summary>
    private static bool Door()
    {
        const string user = "kris";
        const string pass = "pa:ss word";

        var ok = new List<(string Case, bool Passed)>
        {
            ("correct credentials", BasicAuth.Matches(Header(user, pass), user, pass)),
            ("password containing a colon", BasicAuth.Matches(Header(user, "a:b"), user, "a:b")),
            ("wrong password", !BasicAuth.Matches(Header(user, "nope"), user, pass)),
            ("wrong username", !BasicAuth.Matches(Header("someone", pass), user, pass)),
            ("empty password when one is set", !BasicAuth.Matches(Header(user, ""), user, pass)),
            ("no header at all", !BasicAuth.Matches(null, user, pass)),
            ("empty header", !BasicAuth.Matches("", user, pass)),
            ("wrong scheme", !BasicAuth.Matches($"Bearer {Encode($"{user}:{pass}")}", user, pass)),
            ("no base64 payload", !BasicAuth.Matches("Basic", user, pass)),
            ("payload is not base64", !BasicAuth.Matches("Basic not-base-64!", user, pass)),
            ("no colon in the payload", !BasicAuth.Matches($"Basic {Encode("nocolon")}", user, pass))
        };

        var failed = ok.Where(check => !check.Passed).Select(check => check.Case).ToList();

        Console.WriteLine(failed.Count == 0
            ? $"OK: Basic auth answers correctly in all {ok.Count} cases"
            : $"FAIL: Basic auth wrong for {string.Join(", ", failed)}");

        return failed.Count == 0;

        static string Header(string username, string password) => $"Basic {Encode($"{username}:{password}")}";
        static string Encode(string raw) => Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
    }

    /// <summary>
    ///     The failure the panel exists to survive: one target down. The other targets must still
    ///     render, and the dead one must come back as a row saying so rather than an exception — a
    ///     panel that goes blank exactly when something breaks is worse than no panel.
    /// </summary>
    private static async Task<bool> FanInAsync()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ADMIN_TOKEN"] = "self-check",
            ["Targets:0:Name"] = "up",
            ["Targets:0:Url"] = "http://up.invalid",
            ["Targets:1:Name"] = "down",
            ["Targets:1:Url"] = "http://down.invalid",
            ["Targets:2:Name"] = "no-token",
            ["Targets:2:Url"] = "http://no-token.invalid"
        }).Build();

        var fleet = new Fleet(new StubFactory(), configuration, Logger.None);
        // Web defaults, because that is what Results.Json applies on the wire — a check that reads
        // PascalCase would pass while the panel got camelCase and rendered nothing.
        var json = JsonSerializer.SerializeToElement(await fleet.SnapshotAsync(CancellationToken.None),
            JsonSerializerOptions.Web);
        var services = json.GetProperty("services").EnumerateArray().ToList();

        var up = services.Single(service => service.GetProperty("name").GetString() == "up");
        var down = services.Single(service => service.GetProperty("name").GetString() == "down");
        var noToken = services.Single(service => service.GetProperty("name").GetString() == "no-token");

        var ok = services.Count == 3
                 && up.GetProperty("up").GetBoolean()
                 && up.GetProperty("snapshot").GetProperty("count").GetInt32() == 7
                 && !down.GetProperty("up").GetBoolean()
                 && down.GetProperty("error").GetString() is { Length: > 0 }
                 && !noToken.GetProperty("up").GetBoolean()
                 && noToken.GetProperty("error").GetString()!.Contains("ADMIN_TOKEN");

        Console.WriteLine(ok
            ? "OK: one target answering, one refusing, one unreachable -> three rows, no exception"
            : $"FAIL: fan-in returned {json}");

        return ok;
    }

    /// <summary>Answers for the three hosts <see cref="FanInAsync" /> configures, one per outcome.</summary>
    private sealed class StubFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler());

        private sealed class Handler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                var host = request.RequestUri!.Host;

                if (host.StartsWith("down"))
                    throw new HttpRequestException("connection refused");

                if (host.StartsWith("no-token"))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"count":7}""", Encoding.UTF8, "application/json")
                });
            }
        }
    }
}
