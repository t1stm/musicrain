using Serilog;
using Serilog.Templates;
using Serilog.Templates.Themes;
using Stih;

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

// Wide open, like Dunav's: this surface carries no credentials to protect. /register is the one route
// that changes anything, and it is not reachable from a browser at all — see below.
builder.Services.AddCors(options => options.AddPolicy("Frontend", policy => policy
    .AllowAnyOrigin()
    .AllowAnyHeader()
    .AllowAnyMethod()));

// Two clients with different budgets: a pod on the Docker network answers in milliseconds or it is
// down, while lrclib.net is across the internet and its own docs ask for patience rather than retries.
builder.Services.AddHttpClient("pods", http => http.Timeout = TimeSpan.FromSeconds(5));
builder.Services.AddHttpClient("lrclib", http => http.Timeout = TimeSpan.FromSeconds(10));

builder.Services.AddSingleton(services => new LyricsIndex(
    services.GetRequiredService<IConfiguration>()["LYRICS_DATA"] ?? "/lyrics", Log.Logger));
builder.Services.AddSingleton<LrcLib>();
builder.Services.AddSingleton<Tracks>();
builder.Services.AddSingleton<Lyrics>();

builder.Services.AddSingleton<Sweep>();
builder.Services.AddHostedService(services => services.GetRequiredService<Sweep>());

var app = builder.Build();

app.UseCors("Frontend");

// Before the routes, so the request ring wraps the whole pipeline. No-op without ADMIN_TOKEN.
app.MapStihAdmin();

// Mapped on the full public path, like Dom's controllers: nginx proxies /Audio/Lyrics through
// untouched, so pod and public API agree and there is no prefix rewriting to get wrong.
app.MapGet("/Audio/Lyrics/Get", async (string? id, Lyrics lyrics, HttpResponse response, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(id))
        return Results.BadRequest(new ApiErrorBody(new ApiError("invalid_query", "A track id is required.")));

    var trimmed = id.Trim();
    if (!trimmed.Contains("://", StringComparison.Ordinal))
        return Results.BadRequest(new ApiErrorBody(new ApiError("invalid_query",
            "A track id looks like audio://… or deezer://….")));

    var found = await lyrics.GetAsync(trimmed, ct);
    if (found is null) return Results.NoContent();

    // The words for a track do not change, and this is the cheapest possible version of not asking twice.
    response.Headers.CacheControl = "public, max-age=86400";
    return Results.Ok(found);
});

// Local only, in three layers: it is not under /Audio, so no nginx location reaches it; the published
// port is bound to 127.0.0.1; and with ADMIN_TOKEN set it wants the same header Oko uses, through
// Gaida.Admin's constant-time compare rather than a second secret of its own.
app.MapPost("/register", async (RegisterDto body, HttpRequest request, Lyrics lyrics, CancellationToken ct) =>
{
    if (!Authorized(request, app.Configuration)) return Results.Unauthorized();

    var error = await lyrics.RegisterAsync(body, ct);
    return error is null
        ? Results.NoContent()
        : Results.BadRequest(new ApiErrorBody(new ApiError("invalid_query", error)));
});

app.Run();
return 0;

static bool Authorized(HttpRequest request, IConfiguration configuration)
{
    var expected = configuration["ADMIN_TOKEN"];
    if (string.IsNullOrWhiteSpace(expected)) return true;

    var given = request.Headers[Gaida.Admin.AdminApi.TokenHeader].ToString();
    return given.Length == expected.Length &&
           System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
               System.Text.Encoding.UTF8.GetBytes(expected), System.Text.Encoding.UTF8.GetBytes(given));
}
