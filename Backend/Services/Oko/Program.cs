using System.Text;
using System.Text.Json;
using Gaida.Admin;
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
// Who a staff token belongs to. Dom is on the Docker network: a slow answer is a down one.
builder.Services.AddHttpClient("dom", http => http.Timeout = TimeSpan.FromSeconds(5));
builder.Services.AddSingleton<Door>();

// For /Audio/Admin only, which musicrain calls from another origin. Wide open, like Dom's and for Dom's
// reason: the credential is an explicit Authorization header the caller has to already possess, never an
// ambient cookie, so a hostile origin gains nothing by being allowed to ask. Cookies must never be added
// here without replacing this policy with an origin list. The preflight is cached because every request
// carries Authorization, which makes every one of them a preflighted one.
builder.Services.AddCors(options => options.AddPolicy("Frontend", policy => policy
    .AllowAnyOrigin()
    .AllowAnyHeader()
    .AllowAnyMethod()
    .SetPreflightMaxAge(TimeSpan.FromHours(2))));

builder.Services.AddOptions<OkoOptions>().Bind(builder.Configuration);
var settings = builder.Configuration.Get<OkoOptions>() ?? new OkoOptions();
var username = settings.Username;
var password = settings.Password;

// Refuse to start rather than serve an open admin panel. Dunav's /Admin surface disables itself when
// its token is missing because the rest of Dunav still has a job to do; Oko has no job but this one.
if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
{
    Log.Fatal("ADMIN_USERNAME and ADMIN_PASSWORD must both be set. Refusing to start an unauthenticated admin panel");
    return 1;
}

// Same rule for the staff door: without Dom there is nobody to ask who a token belongs to.
if (string.IsNullOrWhiteSpace(settings.DomUrl))
{
    Log.Fatal("Dom:Url must be set. Refusing to start a staff gateway that cannot check who is asking");
    return 1;
}

// A typo here would quietly make that target the owner's alone, so it is a crash-loop instead.
foreach (var target in settings.Targets.Where(target => target.Permission is not null))
    if (!Staff.Permissions.ContainsKey(target.Permission!))
    {
        Log.Fatal("Target {Target} names the permission {Permission}, which Staff.cs does not know. Known: {Known}",
            target.Name, target.Permission, string.Join(", ", Staff.Permissions.Keys));
        return 1;
    }

if (string.IsNullOrWhiteSpace(settings.AdminToken))
    Log.Warning("ADMIN_TOKEN is not set — every target will answer 404 and render as down");

var app = builder.Build();

// First in the pipeline for the old page and its /api: nothing outside /Audio/Admin, static files included,
// is reachable without the Basic password. /Audio/Admin has its own door, Door.cs, which asks Dom.
app.UseWhen(context => !context.Request.Path.StartsWithSegments("/Audio/Admin"), basic => basic.Use(async (context, next) =>
{
    if (!BasicAuth.Matches(context.Request.Headers.Authorization, username, password))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Basic realm=\"Oko\", charset=\"UTF-8\"";
        return;
    }

    await next();
}));

app.UseDefaultFiles();
app.UseStaticFiles();

// After the static files and before the endpoints: the policy only applies where an endpoint asks for it,
// and an OPTIONS preflight is answered here, before the door would refuse it for carrying no token.
app.UseRouting();
app.UseCors();

var fleet = app.Services.GetRequiredService<Fleet>();
var audit = app.Services.GetRequiredService<AuditLog>();

// The Basic user is the owner, so the old page keeps exactly the power it has today.
MapPanel(app.MapGroup("/api"), http => new Caller(BasicUser(http), Staff.Owner, []));

// The staff gateway: the same routes on the full public path, so nginx proxies them through untouched.
MapPanel(app.MapGroup("/Audio/Admin").AddEndpointFilter<Door>().RequireCors("Frontend"),
    http => (Caller)http.Items[typeof(Caller)]!);

Log.Information("Oko watching {Count} target(s): {Targets}",
    fleet.Targets.Count, string.Join(", ", fleet.Targets.Select(target => target.Name)));

app.Run();
return 0;

// These return their values rather than wrapping them in Results.Json, and the ones that need a 404
// spell out `async Task<IResult>`. Both are deliberate: an expression-bodied async lambda taking
// HttpContext has the natural type Func<HttpContext, Task> — which *is* RequestDelegate, so ASP.NET
// invokes it and throws the IResult away. That looks like a working endpoint returning 200 with an
// empty body. CancellationToken binds to RequestAborted on its own, so HttpContext is not needed there.
void MapPanel(RouteGroupBuilder group, Func<HttpContext, Caller> who)
{
    // With the permission each target's actions need, so the panel knows which buttons to enable.
    group.MapGet("/targets", () => fleet.Targets.Select(target => new TargetInfo(target.Name, target.Permission)));

    group.MapGet("/snapshot", fleet.SnapshotAsync);

    group.MapGet("/requests/{name}", async Task<IResult> (string name, CancellationToken cancellationToken) =>
    {
        var requests = await fleet.RequestsAsync(name, cancellationToken);
        return requests is null ? Results.NotFound() : Results.Json(requests);
    });

    // Every mutation in the stack goes through here, and every one is recorded before the answer is
    // returned — including the failures, because a rejected delete is worth seeing too, and so is an admin
    // trying a button they were not given. The action is a single route segment, so it names one of the
    // target's own /Admin routes and cannot walk anywhere else.
    group.MapPost("/action/{name}/{action}", async Task<IResult> (
        string name, string action, HttpContext http, CancellationToken cancellationToken) =>
    {
        var op = who(http);
        var target = fleet.Find(name);

        var (status, body) = target is not null && !op.Can(target.Permission)
            ? (StatusCodes.Status403Forbidden, JsonSerializer.Serialize(new
            {
                error = new
                {
                    code = "missing_permission",
                    message = target.Permission is null
                        ? $"Only the owner can act on {target.Name}."
                        : $"Acting on {target.Name} needs the {target.Permission} permission."
                }
            }))
            : await fleet.ActAsync(name, action, http.Request.QueryString, cancellationToken);

        audit.Record(new AuditEntry(DateTimeOffset.UtcNow, op.Name, name, action,
            AuditLog.Describe(http.Request.Query), status, status < 400 ? null : body));

        return Results.Content(body, "application/json", statusCode: status);
    });

    // Reads are forwarded but not audited — see Fleet.ReadAsync. Every staff account may read.
    group.MapGet("/read/{name}/{action}", async Task<IResult> (
        string name, string action, HttpContext http, CancellationToken cancellationToken) =>
    {
        var (status, body) = await fleet.ReadAsync(name, action, http.Request.QueryString, cancellationToken);
        return Results.Content(body, "application/json", statusCode: status);
    });

    group.MapGet("/audit", () => audit.Recent().Reverse());

    // Opens the upstream feeds on subscribe and closes them on disconnect — see Fleet.FollowAsync. The header
    // keeps nginx streaming it even if a location ever forgets `proxy_buffering off`.
    group.MapGet("/events", (HttpContext http) =>
    {
        http.Response.Headers["X-Accel-Buffering"] = "no";
        return TypedResults.ServerSentEvents(fleet.FollowAsync(http.RequestAborted), "activity");
    });
}

// One operator on the old door, so this is always the configured username — but the audit log records
// who asked, not what the password was.
static string BasicUser(HttpContext http)
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
