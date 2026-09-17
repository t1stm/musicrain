using System.Text;
using Oko;
using Serilog;
using Serilog.Templates;
using Serilog.Templates.Themes;

Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .WriteTo.Console(new ExpressionTemplate(
        "[{@t:HH:mm:ss} {@l:u3}" +
        "{#if SourceContext is not null} {Substring(SourceContext, LastIndexOf(SourceContext, '.') + 1)}{#end}] {@m}\n{@x}",
        theme: TemplateTheme.Code))
    .CreateLogger();

if (args.Contains("--self-check")) return await SelfCheck.RunAsync() ? 0 : 1;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSerilog();
builder.Services.AddSingleton(Log.Logger);
builder.Services.AddSingleton<Fleet>();
builder.Services.AddSingleton<AuditLog>();

// Two clients because they want opposite timeouts. A snapshot that hangs must give up so one dead
// service cannot stall the page; a feed is supposed to hang, and its lifetime is the subscriber's
// cancellation token instead.
builder.Services.AddHttpClient("snapshot", http => http.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddHttpClient("events", http => http.Timeout = Timeout.InfiniteTimeSpan);

// No CORS policy, unlike every other service here: nothing cross-origin should ever reach this one.

var username = builder.Configuration["ADMIN_USERNAME"];
var password = builder.Configuration["ADMIN_PASSWORD"];

// Refuse to start rather than serve an open admin panel. Dunav's /Admin surface disables itself when
// its token is missing because the rest of Dunav still has a job to do; Oko has no job but this one.
if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
{
    Log.Fatal("ADMIN_USERNAME and ADMIN_PASSWORD must both be set. Refusing to start an unauthenticated admin panel");
    return 1;
}

if (string.IsNullOrWhiteSpace(builder.Configuration["ADMIN_TOKEN"]))
    Log.Warning("ADMIN_TOKEN is not set — every target will answer 404 and render as down");

var app = builder.Build();

// First in the pipeline: nothing below it, static files included, is reachable unauthenticated.
app.Use(async (context, next) =>
{
    if (!BasicAuth.Matches(context.Request.Headers.Authorization, username, password))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Basic realm=\"Oko\", charset=\"UTF-8\"";
        return;
    }

    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();

var fleet = app.Services.GetRequiredService<Fleet>();
var audit = app.Services.GetRequiredService<AuditLog>();


// These return their values rather than wrapping them in Results.Json, and the one that needs a 404
// spells out `async Task<IResult>`. Both are deliberate: an expression-bodied async lambda taking
// HttpContext has the natural type Func<HttpContext, Task> — which *is* RequestDelegate, so ASP.NET
// invokes it and throws the IResult away. That looks like a working endpoint returning 200 with an
// empty body. CancellationToken binds to RequestAborted on its own, so HttpContext is not needed here.
app.MapGet("/api/targets", () => fleet.Targets.Select(target => target.Name));

app.MapGet("/api/snapshot", fleet.SnapshotAsync);

app.MapGet("/api/requests/{name}", async Task<IResult> (string name, CancellationToken cancellationToken) =>
{
    var requests = await fleet.RequestsAsync(name, cancellationToken);
    return requests is null ? Results.NotFound() : Results.Json(requests);
});

// Every mutation in the stack goes through here, and every one is recorded before the answer is
// returned — including the failures, because a rejected delete is worth seeing too. The action is a
// single route segment, so it names one of the target's own /Admin routes and cannot walk anywhere else.
app.MapPost("/api/action/{name}/{action}", async Task<IResult> (
    string name, string action, HttpContext http, CancellationToken cancellationToken) =>
{
    var (status, body) = await fleet.ActAsync(name, action, http.Request.QueryString, cancellationToken);

    audit.Record(new AuditEntry(DateTimeOffset.UtcNow, Operator(http), name, action,
        AuditLog.Describe(http.Request.Query), status, status < 400 ? null : body));

    return Results.Content(body, "application/json", statusCode: status);
});

// Reads are forwarded but not audited — see Fleet.ReadAsync.
app.MapGet("/api/read/{name}/{action}", async Task<IResult> (
    string name, string action, HttpContext http, CancellationToken cancellationToken) =>
{
    var (status, body) = await fleet.ReadAsync(name, action, http.Request.QueryString, cancellationToken);
    return Results.Content(body, "application/json", statusCode: status);
});

app.MapGet("/api/audit", () => audit.Recent().Reverse());

// Opens the upstream feeds on subscribe and closes them on disconnect — see Fleet.FollowAsync.
app.MapGet("/api/events", (HttpContext http) =>
    TypedResults.ServerSentEvents(fleet.FollowAsync(http.RequestAborted), "activity"));

Log.Information("Oko watching {Count} target(s): {Targets}",
    fleet.Targets.Count, string.Join(", ", fleet.Targets.Select(target => target.Name)));

app.Run();
return 0;

// One operator today, so this is always the configured username — but the audit log records who
// asked, not what the password was, and that stays true if a second account ever shows up.
static string Operator(HttpContext http)
{
    var header = http.Request.Headers.Authorization.ToString();
    if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return "unknown";

    try
    {
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..].Trim()));
        var separator = decoded.IndexOf(':');
        return separator < 0 ? "unknown" : decoded[..separator];
    }
    catch (FormatException)
    {
        return "unknown";
    }
}
