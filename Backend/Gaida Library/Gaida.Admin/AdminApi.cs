using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Gaida.Admin;

/// <summary>
///     The half of the admin surface that is identical in every service: the shared-secret check, the
///     request ring, and the live feed. A service calls <see cref="MapAdmin(WebApplication,Func{object})" /> once and adds its own
///     routes to the group it gets back.
/// </summary>
/// <remarks>
///     This is the one abstraction ADMIN_PLAN.md adds, and it exists for the token comparison: Selo and
///     Dom do not reference Gaida.Core, so the alternative is five hand-copied constant-time compares
///     free to drift apart. The ring and the channel come along because they are the same code five
///     times over, not because they might be reused later.
///     <para>
///         Everything here is pull: the service answers when asked and pushes nothing. Nothing in this
///         assembly knows the admin panel's address, or that it exists. See ADMIN_PLAN.md.
///     </para>
/// </remarks>
public static class AdminApi
{
    /// <summary>The header Oko authenticates itself with. Not <c>Authorization</c> — that one is the operator's.</summary>
    public const string TokenHeader = "X-Admin-Token";

    /// <summary>
    ///     Installs the request ring and maps <c>/Admin/snapshot</c>, <c>/Admin/requests</c> and
    ///     <c>/Admin/events</c>. Call it before <c>MapControllers</c> so the ring sees the whole pipeline.
    /// </summary>
    /// <param name="app">The service's application, before <c>MapControllers</c>.</param>
    /// <param name="snapshot">
    ///     Whatever this service wants an operator to see, serialised as JSON on demand. Called per request
    ///     and never cached: the service's own state is the only copy, and a second one would just be staler.
    /// </param>
    /// <returns>
    ///     The <c>/Admin</c> group, with the token filter already applied, for the service to hang its own
    ///     routes on — or <c>null</c> when <c>ADMIN_TOKEN</c> is unset, in which case nothing is mapped at
    ///     all. Fail closed: no secret, no admin surface.
    /// </returns>
    public static RouteGroupBuilder? MapAdmin(this WebApplication app, Func<object?> snapshot)
    {
        return app.MapAdmin(() => Task.FromResult(snapshot()));
    }

    /// <inheritdoc cref="MapAdmin(WebApplication,Func{object})" />
    /// <remarks>
    ///     For services whose state is behind an async lock — Selo reads its rooms under the same
    ///     <c>SemaphoreSlim</c> the player uses, rather than racing a <c>List&lt;T&gt;</c> being mutated.
    /// </remarks>
    public static RouteGroupBuilder? MapAdmin(this WebApplication app, Func<Task<object?>> snapshot)
    {
        var token = app.Configuration["ADMIN_TOKEN"];
        if (string.IsNullOrWhiteSpace(token))
        {
            app.Logger.LogInformation("ADMIN_TOKEN is not set — the /Admin surface is disabled.");
            return null;
        }

        var feed = new AdminFeed();
        var expected = Encoding.UTF8.GetBytes(token);

        app.Use(async (context, next) =>
        {
            // The admin routes are excluded from their own ring on purpose: /Admin/events recording itself
            // would feed every SSE frame back into the stream that produced it.
            if (context.Request.Path.StartsWithSegments("/Admin"))
            {
                await next();
                return;
            }

            var started = Stopwatch.GetTimestamp();
            try
            {
                await next();
            }
            finally
            {
                feed.Record(new RequestEntry(DateTimeOffset.UtcNow, context.Request.Method,
                    context.Request.Path + context.Request.QueryString, context.Response.StatusCode,
                    (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds));
            }
        });

        var group = app.MapGroup("/Admin");

        // 404, not 401: a scanner without the token cannot tell the surface is there at all.
        group.AddEndpointFilter(async (context, next) =>
        {
            var given = context.HttpContext.Request.Headers[TokenHeader].ToString();
            return Matches(expected, given) ? await next(context) : Results.NotFound();
        });

        // Explicit Task<IResult>: an expression-bodied async lambda can infer a bare Task, and a
        // handler ASP.NET reads as a RequestDelegate has its result discarded — 200, empty body.
        group.MapGet("/snapshot", async Task<IResult> () => Results.Json(await snapshot()));
        group.MapGet("/requests", () => Results.Json(feed.Recent()));
        group.MapGet("/events", (HttpContext http) =>
            TypedResults.ServerSentEvents(feed.Subscribe(http.RequestAborted), "request"));

        return group;
    }

    /// <summary>Constant-time, so a wrong token cannot be walked one byte at a time off the response latency.</summary>
    private static bool Matches(byte[] expected, string given)
    {
        return given.Length > 0 &&
               CryptographicOperations.FixedTimeEquals(expected, Encoding.UTF8.GetBytes(given));
    }
}

/// <summary>One handled request, as an operator wants to read it.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record RequestEntry(DateTimeOffset At, string Method, string Path, int Status, long Ms);

/// <summary>
///     The last <see cref="Capacity" /> requests, plus a live fan-out of them to whoever is watching.
/// </summary>
/// <remarks>
///     Bounded by construction in both directions, which is what makes an unwatched panel free: the ring
///     wraps in place and never grows, and the per-subscriber channels only exist while a subscriber does.
///     Nothing here is written to disk and nothing survives a restart.
/// </remarks>
public sealed class AdminFeed
{
    /// <summary>~50 KB. Long enough to see what just happened, short enough to never be a memory question.</summary>
    private const int Capacity = 500;

    private readonly Lock _gate = new();
    private readonly Queue<RequestEntry> _recent = new(Capacity);
    private readonly List<Channel<RequestEntry>> _subscribers = [];

    public void Record(RequestEntry entry)
    {
        lock (_gate)
        {
            if (_recent.Count == Capacity) _recent.Dequeue();
            _recent.Enqueue(entry);

            // TryWrite on a DropOldest channel never blocks and never fails, so a subscriber that has
            // stopped reading costs the request path nothing. This is the whole reason the panel pulls:
            // there is no queue here that a slow or absent watcher can grow.
            foreach (var subscriber in _subscribers) subscriber.Writer.TryWrite(entry);
        }
    }

    public RequestEntry[] Recent()
    {
        lock (_gate) return _recent.ToArray();
    }

    /// <summary>Requests as they happen, for as long as the caller stays connected.</summary>
    public async IAsyncEnumerable<RequestEntry> Subscribe([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateBounded<RequestEntry>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

        lock (_gate) _subscribers.Add(channel);

        try
        {
            await foreach (var entry in channel.Reader.ReadAllAsync(cancellationToken)) yield return entry;
        }
        finally
        {
            lock (_gate) _subscribers.Remove(channel);
        }
    }
}
