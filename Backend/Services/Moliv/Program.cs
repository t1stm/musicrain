using Gaida.Admin;
using Microsoft.Extensions.Options;
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

builder.Services.AddOptions<MolivOptions>().Bind(builder.Configuration);
builder.Services.AddSingleton(services => new Plays(
    services.GetRequiredService<IOptions<MolivOptions>>().Value.DataFile));
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
    var who = await identity.WhoAsync(request, ct);
    if (!who.Known) return who.Refusal;

    if (!Guid.TryParseExact(id, "D", out var play)) return Api.Invalid("The play id must be a UUID.");
    if (body is null) return Api.Invalid("Send the play as JSON.");

    var row = body.ToRow(play.ToString(), who.Caller, DateTimeOffset.UtcNow);
    if (!row.Ok) return Api.Invalid(row.Error);

    return plays.Upsert(row.Row)
        ? Results.NoContent()
        : Api.Error(403, "forbidden", "That play was recorded on another device.");
});

app.MapGet("/Audio/History", async (string? before, int? limit, HttpRequest request, CancellationToken ct) =>
{
    var who = await identity.WhoAsync(request, ct);
    if (!who.Known) return who.Refusal;

    if (limit is < 1 or > 100) return Api.Invalid("limit must be between 1 and 100.");
    var cursor = before is null ? null : Api.ParseCursor(before);
    if (before is not null && cursor is null) return Api.Invalid("before is not a cursor this service handed out.");

    var page = plays.List(who.Caller, cursor, limit ?? 50);
    return Results.Ok(new PageDto(page, page.Count == (limit ?? 50) ? Api.Cursor(page[^1]) : null));
});

app.MapGet("/Audio/History/Recent", async (int? limit, HttpRequest request, CancellationToken ct) =>
{
    var who = await identity.WhoAsync(request, ct);
    if (!who.Known) return who.Refusal;

    return limit is < 1 or > 50
        ? Api.Invalid("limit must be between 1 and 50.")
        : Results.Ok(plays.Recent(who.Caller, limit ?? 12));
});

app.MapPost("/Audio/History/Claim", async (HttpRequest request, CancellationToken ct) =>
{
    var who = await identity.WhoAsync(request, ct);
    if (!who.Known) return who.Refusal;

    return who.Caller.UserId is { } user
        ? Results.Ok(new { claimed = plays.Claim(user, who.Caller.DeviceId) })
        : Api.Error(401, "unauthorized", "Sign in first.");
});

app.MapDelete("/Audio/History", async (HttpRequest request, CancellationToken ct) =>
{
    var who = await identity.WhoAsync(request, ct);
    return who.Known ? Results.Ok(new { deleted = plays.Clear(who.Caller) }) : who.Refusal;
});

// Local only, like Stih's /register: not under /Audio, so no nginx location reaches it; the published port
// is bound to 127.0.0.1; and with ADMIN_TOKEN set it wants the header Oko uses. Dom calls it when an
// account is deleted.
app.MapPost("/forget", (string? user, HttpRequest request, IOptions<MolivOptions> options) =>
{
    if (!AdminApi.Authorized(request, options.Value.AdminToken)) return Results.Unauthorized();

    return string.IsNullOrWhiteSpace(user)
        ? Api.Invalid("Name the account to forget.")
        : Results.Ok(new { deleted = plays.Forget(user) });
});

app.Run();
return 0;
