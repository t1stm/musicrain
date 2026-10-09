using Gaida.Admin;
using Moliv;
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

if (args.Contains("--self-check")) return SelfCheck.Run() ? 0 : 1;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSerilog();
builder.Services.AddSingleton(Log.Logger);
builder.Services.AddMemoryCache();

// The largest body here is one play, a few hundred bytes. Anonymous writes need no account, so this is
// also what keeps one request from being a large one.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = 4096);

// Wide open, like Dom's and for Dom's reason: the credential is an explicit Authorization header the
// caller already holds, never a cookie. The preflight is cached because the app is on another origin and
// X-Device-Id makes every request a preflighted one — uncached, each play would cost two round trips.
builder.Services.AddCors(options => options.AddPolicy("Frontend", policy => policy
    .AllowAnyOrigin()
    .AllowAnyHeader()
    .AllowAnyMethod()
    .SetPreflightMaxAge(TimeSpan.FromHours(2))));

// Dom is on the Docker network: a slow answer is a down one, and the client's outbox retries.
builder.Services.AddHttpClient("dom", http => http.Timeout = TimeSpan.FromSeconds(5));

builder.Services.AddSingleton(services => new Plays(
    services.GetRequiredService<IConfiguration>()["Moliv:DataFile"] ?? "plays.db"));
builder.Services.AddSingleton<Identity>();

var app = builder.Build();

app.UseCors("Frontend");

// open the database at boot, so a broken one fails the container rather than the first play
var plays = app.Services.GetRequiredService<Plays>();
var identity = app.Services.GetRequiredService<Identity>();

// Before the routes, so the request ring wraps the whole pipeline. No-op without ADMIN_TOKEN.
app.MapAdmin(() => new { service = "moliv", plays = plays.Snapshot(), dom = new { lastError = identity.LastError } });

// Mapped on the full public path, like Stih's: nginx proxies /Audio/History through untouched.

app.MapPut("/Audio/History/Plays/{id}", async (string id, PlayBody? body, HttpRequest request, CancellationToken ct) =>
{
    var (caller, refusal) = await identity.WhoAsync(request, ct);
    if (refusal is not null) return refusal;

    if (!Guid.TryParseExact(id, "D", out var play)) return Invalid("The play id must be a UUID.");
    if (body is null) return Invalid("Send the play as JSON.");

    var (row, error) = body.ToRow(play.ToString(), caller!, DateTimeOffset.UtcNow);
    if (error is not null) return Invalid(error);

    return plays.Upsert(row!)
        ? Results.NoContent()
        : Api.Error(403, "forbidden", "That play was recorded on another device.");
});

app.MapGet("/Audio/History", async (string? before, int? limit, HttpRequest request, CancellationToken ct) =>
{
    var (caller, refusal) = await identity.WhoAsync(request, ct);
    if (refusal is not null) return refusal;

    if (limit is < 1 or > 100) return Invalid("limit must be between 1 and 100.");
    var cursor = before is null ? null : Api.ParseCursor(before);
    if (before is not null && cursor is null) return Invalid("before is not a cursor this service handed out.");

    var page = plays.List(caller!, cursor, limit ?? 50);
    return Results.Ok(new PageDto(page, page.Count == (limit ?? 50) ? Api.Cursor(page[^1]) : null));
});

app.MapGet("/Audio/History/Recent", async (int? limit, HttpRequest request, CancellationToken ct) =>
{
    var (caller, refusal) = await identity.WhoAsync(request, ct);
    if (refusal is not null) return refusal;

    return limit is < 1 or > 50
        ? Invalid("limit must be between 1 and 50.")
        : Results.Ok(plays.Recent(caller!, limit ?? 12));
});

app.MapPost("/Audio/History/Claim", async (HttpRequest request, CancellationToken ct) =>
{
    var (caller, refusal) = await identity.WhoAsync(request, ct);
    if (refusal is not null) return refusal;

    return caller!.UserId is { } user
        ? Results.Ok(new { claimed = plays.Claim(user, caller.DeviceId) })
        : Api.Error(401, "unauthorized", "Sign in first.");
});

app.MapDelete("/Audio/History", async (HttpRequest request, CancellationToken ct) =>
{
    var (caller, refusal) = await identity.WhoAsync(request, ct);
    return refusal ?? Results.Ok(new { deleted = plays.Clear(caller!) });
});

// Local only, like Stih's /register: not under /Audio, so no nginx location reaches it; the published port
// is bound to 127.0.0.1; and with ADMIN_TOKEN set it wants the header Oko uses. Dom calls it when an
// account is deleted.
app.MapPost("/forget", (string? user, HttpRequest request) =>
{
    if (!AdminApi.Authorized(request, app.Configuration["ADMIN_TOKEN"])) return Results.Unauthorized();

    return string.IsNullOrWhiteSpace(user)
        ? Invalid("Name the account to forget.")
        : Results.Ok(new { deleted = plays.Forget(user) });
});

app.Run();
return 0;

static IResult Invalid(string message) => Api.Error(400, "invalid_request", message);
