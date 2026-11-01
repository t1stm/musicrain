using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Serilog.Core;

namespace Oko;

/// <summary>
///     The pieces of Oko that are worth proving runnable: both doors, and the fan-in's behaviour when a
///     service is down. Run with <c>dotnet run --project Oko -- --self-check</c>.
/// </summary>
internal static class SelfCheck
{
    public static async Task<bool> RunAsync()
    {
        return Door() & await StaffDoorAsync() & await FanInAsync() & Audit();
    }

    /// <summary>
    ///     The bearer door against a stub Dom: who gets in, who is refused with what, and what an admin may
    ///     then act on. The permission cases run on the <see cref="Caller" /> the door hands the handlers,
    ///     with the targets' permissions as compose sets them.
    /// </summary>
    private static async Task<bool> StaffDoorAsync()
    {
        var door = new Door(new StubFactory(DomAnswer, TimeSpan.FromMilliseconds(200)),
            Options.Create(new OkoOptions { DomUrl = "http://dom.invalid" }));

        async Task<int> Status(string authorization) =>
            (await door.WhoAsync(authorization, CancellationToken.None)).Refusal?.Status ?? 200;
        async Task<Caller> Who(string token) =>
            (await door.WhoAsync($"Bearer {token}", CancellationToken.None)).Who!;

        var admin = await Who("caches");
        var owner = await Who("owner");

        var checks = new List<(string Case, bool Passed)>
        {
            ("no header is 401", await Status("") == 401),
            ("a Basic header is 401", await Status("Basic a3JpczpodW50ZXIy") == 401),
            ("a token Dom refuses is 401", await Status("Bearer expired") == 401),
            ("an account with no role is 403", await Status("Bearer listener") == 403),
            ("Dom timing out is 503", await Status("Bearer slow") == 503),
            ("Dom answering junk is 503", await Status("Bearer junk") == 503),
            ("an admin passes the door, so may read", await Status("Bearer caches") == 200),
            ("an admin with caches may act on dunav", admin.Can("caches")),
            ("an admin with caches may not act on dom", !admin.Can("accounts")),
            ("an admin may not act on a target with no permission", !admin.Can(null)),
            ("the owner may act on dom", owner.Can("accounts")),
            ("the owner may act on a target with no permission", owner.Can(null)),
            ("the audit names the account", admin.Name == "ana")
        };

        var failed = checks.Where(check => !check.Passed).Select(check => check.Case).ToList();
        Console.WriteLine(failed.Count == 0
            ? $"OK: the staff door answers correctly in all {checks.Count} cases"
            : $"FAIL: staff door wrong for {string.Join(", ", failed)}");
        return failed.Count == 0;

        // Dom's /Me, by token.
        static async Task<HttpResponseMessage> DomAnswer(HttpRequestMessage request, CancellationToken ct)
        {
            static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };

            switch (request.Headers.Authorization?.Parameter)
            {
                case "slow":
                    await Task.Delay(Timeout.Infinite, ct);
                    return new HttpResponseMessage(HttpStatusCode.OK);
                case "junk":
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>") };
                case "listener":
                    return Json("""{"id":"1","username":"lu","role":null,"permissions":[]}""");
                case "caches":
                    return Json("""{"id":"2","username":"ana","role":"admin","permissions":["caches"]}""");
                case "owner":
                    return Json($$"""{"id":"3","username":"kris","role":"owner","permissions":{{
                        JsonSerializer.Serialize(Gaida.Admin.Staff.Permissions.Keys)}}}""");
                default:
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }
        }
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

        var options = new OkoOptions();
        configuration.Bind(options);

        var fleet = new Fleet(new StubFactory(FanInAnswer), Options.Create(options), Logger.None);
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
    private static Task<HttpResponseMessage> FanInAnswer(HttpRequestMessage request, CancellationToken ct)
    {
        var host = request.RequestUri!.Host;

        if (host.StartsWith("down", StringComparison.Ordinal))
            throw new HttpRequestException("connection refused");

        if (host.StartsWith("no-token", StringComparison.Ordinal))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"count":7}""", Encoding.UTF8, "application/json")
        });
    }

    /// <summary>Every client it hands out answers with <paramref name="answer" />, within <paramref name="timeout" />.</summary>
    private sealed class StubFactory(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer,
        TimeSpan? timeout = null) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(new Handler(answer)) { Timeout = timeout ?? TimeSpan.FromSeconds(100) };

        private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer)
            : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken) => answer(request, cancellationToken);
        }
    }
}
