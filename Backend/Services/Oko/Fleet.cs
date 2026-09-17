using System.Net.Http.Headers;
using System.Net.ServerSentEvents;
using System.Text.Json;
using System.Threading.Channels;
using JetBrains.Annotations;
using ILogger = Serilog.ILogger;

namespace Oko;

/// <summary>One service Oko watches. Name is what the panel shows; Url is where it lives.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class Target
{
    public required string Name { get; set; }
    public required string Url { get; set; }
}

/// <summary>
///     Every service Oko watches, and the three things it does with them: read a snapshot, read a
///     request ring, follow a live feed.
/// </summary>
/// <remarks>
///     Nothing here runs on a timer. Every method is driven by an open browser — close the tab and Oko
///     stops talking to anything at all. That is the whole reason the panel pulls instead of the
///     services pushing: there is no state to keep warm, so there is no reason to poll.
/// </remarks>
public sealed class Fleet(IHttpClientFactory factory, IConfiguration configuration, ILogger log)
{
    /// <summary>How long a target has to answer a snapshot before it is rendered as down.</summary>
    private static readonly TimeSpan SnapshotTimeout = TimeSpan.FromSeconds(5);

    private readonly string _token = configuration["ADMIN_TOKEN"] ?? "";

    public IReadOnlyList<Target> Targets { get; } =
        configuration.GetSection("Targets").Get<Target[]>() ?? [];

    /// <summary>
    ///     Every target's snapshot, fanned out in parallel. A target that is down, slow or answering
    ///     nonsense becomes one row saying so — it never fails the page, because the whole point of
    ///     opening the panel is often that something is broken.
    /// </summary>
    public async Task<FleetSnapshot> SnapshotAsync(CancellationToken cancellationToken)
    {
        var services = await Task.WhenAll(Targets.Select(async target =>
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(SnapshotTimeout);

                var snapshot = await GetJsonAsync(target, "/Admin/snapshot", timeout.Token);
                return new ServiceStatus(target.Name, target.Url, true, snapshot, null);
            }
            catch (Exception exception) when (exception is not OperationCanceledException ||
                                              !cancellationToken.IsCancellationRequested)
            {
                log.Debug(exception, "Snapshot failed for {Target}", target.Name);
                return new ServiceStatus(target.Name, target.Url, false, null, Describe(exception));
            }
        }));

        return new FleetSnapshot(DateTimeOffset.UtcNow, services);
    }

    /// <summary>
    ///     Forwards one action to a target's <c>/Admin/{action}</c> with the query string it was given.
    /// </summary>
    /// <remarks>
    ///     Generic on purpose, and safe because of what it cannot reach: <paramref name="action" /> is a
    ///     single route segment, so it names one of that service's own admin routes and nothing else.
    ///     Oko does not need to know what the actions mean — the service that owns the data validates
    ///     them, and an unknown one is that service's 404.
    /// </remarks>
    public Task<(int status, string body)> ActAsync(string name, string action, QueryString query,
        CancellationToken cancellationToken)
    {
        return SendAsync(HttpMethod.Post, name, action, query, cancellationToken);
    }

    /// <summary>
    ///     The same forwarding for a read. Separate from <see cref="ActAsync" /> only so that the caller
    ///     records one and not the other: an audit log that fills up with "looked at the library" is an
    ///     audit log nobody reads the deletions out of.
    /// </summary>
    public Task<(int status, string body)> ReadAsync(string name, string action, QueryString query,
        CancellationToken cancellationToken)
    {
        return SendAsync(HttpMethod.Get, name, action, query, cancellationToken);
    }

    private async Task<(int status, string body)> SendAsync(HttpMethod method, string name, string action,
        QueryString query, CancellationToken cancellationToken)
    {
        var target = Find(name);
        if (target is null) return (StatusCodes.Status404NotFound, """{"error":"no such target"}""");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SnapshotTimeout);

        try
        {
            var http = factory.CreateClient("snapshot");
            using var request = Request(target, $"/Admin/{action}{query}");
            request.Method = method;

            using var response = await http.SendAsync(request, timeout.Token);
            return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(timeout.Token));
        }
        catch (Exception exception)
        {
            log.Warning(exception, "{Method} {Action} failed on {Target}", method, action, name);
            return (StatusCodes.Status502BadGateway,
                JsonSerializer.Serialize(new { error = Describe(exception) }));
        }
    }

    /// <summary>One target's request ring, passed through untouched.</summary>
    public async Task<JsonElement?> RequestsAsync(string name, CancellationToken cancellationToken)
    {
        var target = Find(name);
        if (target is null) return null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SnapshotTimeout);

        return await GetJsonAsync(target, "/Admin/requests", timeout.Token);
    }

    /// <summary>
    ///     One merged stream of every target's live feed, for as long as the caller is listening.
    /// </summary>
    /// <remarks>
    ///     The upstream connections are opened when a browser subscribes and closed when it goes away,
    ///     because every pump is tied to <paramref name="cancellationToken" />. Nobody watching means no
    ///     connections at all — not an idle pool, not a reconnect loop.
    /// </remarks>
    public IAsyncEnumerable<FeedItem> FollowAsync(CancellationToken cancellationToken)
    {
        // DropOldest: a browser that stops reading loses the oldest lines rather than stalling the
        // pumps, which is the same rule the services apply to their own subscribers.
        var channel = Channel.CreateBounded<FeedItem>(new BoundedChannelOptions(512)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

        var pumps = Targets.Select(target => PumpAsync(target, channel.Writer, cancellationToken));
        _ = Task.WhenAll(pumps).ContinueWith(_ => channel.Writer.TryComplete(), TaskScheduler.Default);

        return channel.Reader.ReadAllAsync(cancellationToken);
    }

    /// <summary>
    ///     Follows one target's <c>/Admin/events</c>, reconnecting while the subscriber is still there.
    ///     A service that is down or restarting is a gap in the feed, not the end of it.
    /// </summary>
    private async Task PumpAsync(Target target, ChannelWriter<FeedItem> writer,
        CancellationToken cancellationToken)
    {
        var http = factory.CreateClient("events");

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var request = Request(target, "/Admin/events");
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                response.EnsureSuccessStatusCode();

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var parser = SseParser.Create(stream, (_, bytes) => JsonSerializer.Deserialize<JsonElement>(bytes));

                await foreach (var item in parser.EnumerateAsync(cancellationToken))
                    writer.TryWrite(new FeedItem(target.Name, item.EventType, item.Data));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                log.Debug(exception, "Feed dropped for {Target}", target.Name);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private Target? Find(string name)
    {
        return Targets.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<JsonElement> GetJsonAsync(Target target, string path, CancellationToken cancellationToken)
    {
        var http = factory.CreateClient("snapshot");
        using var request = Request(target, path);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
    }

    private HttpRequestMessage Request(Target target, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, target.Url.TrimEnd('/') + path);
        request.Headers.Add("X-Admin-Token", _token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    /// <summary>
    ///     What to show in the panel when a target does not answer. A 404 gets its own wording: that is
    ///     what a service with no <c>ADMIN_TOKEN</c> configured looks like from out here, and it is by
    ///     far the likeliest cause of a target that is plainly running but says nothing.
    /// </summary>
    private static string Describe(Exception exception)
    {
        return exception switch
        {
            HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound } =>
                "no /Admin surface — the service is up but its ADMIN_TOKEN is unset or does not match",
            OperationCanceledException => $"no answer within {SnapshotTimeout.TotalSeconds:0}s",
            HttpRequestException http => http.HttpRequestError.ToString(),
            _ => exception.Message
        };
    }
}

/// <summary>Every target as of one fan-out. A concrete type on purpose — see <see cref="ServiceStatus" />.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record FleetSnapshot(DateTimeOffset At, ServiceStatus[] Services);

/// <summary>
///     One service as the panel renders it: answering with a snapshot, or down with a reason.
/// </summary>
/// <remarks>
///     Records rather than anonymous types because these cross the wire: handing
///     <c>Results.Json</c> a value whose static type is <c>object</c> serialises to an empty body,
///     with a 200 and no content type — which looks exactly like a working endpoint until the panel
///     renders nothing.
/// </remarks>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record ServiceStatus(string Name, string Url, bool Up, JsonElement? Snapshot, string? Error);

/// <summary>One line of the merged feed: which service it came from, and what it said.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record FeedItem(string Target, string EventType, JsonElement Data);
