using Gaida.Admin;
using Serilog;
using Serilog.Templates;
using Serilog.Templates.Themes;
using SixLabors.ImageSharp.Web;
using SixLabors.ImageSharp.Web.Caching;
using Snimka;

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

// Album_Covers/ the library's originals, remote/ everything fetched from elsewhere, cache/ the resizes
// ImageSharp.Web makes of both. Only the first is anything but a cache.
var data = Path.GetFullPath(builder.Configuration["SNIMKA_DATA"] is { Length: > 0 } set ? set : "data");
var covers = new Covers(Path.Combine(data, "Album_Covers"));
var cache = Path.Combine(data, "cache");

// The hosts the pods' thumbnails are on: YouTube's two, Deezer's CDN, Spotify's. A new pod whose artwork
// lives somewhere else adds its host here, or its images answer 400.
var hosts = (builder.Configuration["SNIMKA_HOSTS"] is { Length: > 0 } listed
        ? listed
        : "ytimg.com,img.youtube.com,dzcdn.net,scdn.co,spotifycdn.com")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

// No redirects: an allowed host must not be able to send this somewhere that is not allowed.
builder.Services.AddHttpClient(Remote.Client, http => http.Timeout = TimeSpan.FromSeconds(10))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton(services => new Remote(Path.Combine(data, "remote"),
    services.GetRequiredService<IHttpClientFactory>(), Log.Logger));
builder.Services.AddSingleton(covers);
builder.Services.AddSingleton(services => new Images(covers, services.GetRequiredService<Remote>(), hosts));

// Every cover is named by the SHA-1 of its bytes and every remote image is kept as first fetched, so
// nothing under a URL here ever changes: the browser caches it for good and never asks again.
const string Forever = "public, max-age=31536000, immutable";

builder.Services.AddImageSharp(options =>
    {
        options.BrowserMaxAge = TimeSpan.FromDays(365);
        // The default adds autoorient=true to every request that names none, which would re-encode every
        // request for an original at quality 75 and keep a copy. Without it a request with no size carries
        // no commands and falls through to the endpoint below, which sends the file as it is.
        options.OnParseCommandsAsync = _ => Task.CompletedTask;
        options.OnPrepareResponseAsync = context =>
        {
            context.Response.Headers.CacheControl = Forever;
            return Task.CompletedTask;
        };
    })
    .SetRequestParser<SizeParser>()
    .SetCacheKey<CoverCacheKey>()
    .ClearProviders()
    .AddProvider<CoverProvider>()
    .Configure<PhysicalFileSystemCacheOptions>(options =>
    {
        options.CacheRootPath = data;
        options.CacheFolder = "cache";
    });

var app = builder.Build();

// Before everything else, so the request ring sees the image requests too. No-op without ADMIN_TOKEN.
var remote = app.Services.GetRequiredService<Remote>();
app.MapAdmin(() => new
{
    service = "snimka",
    covers = Measure(covers.Root),
    remote = Measure(remote.Root),
    remoteMissing = remote.Missing,
    cache = Measure(cache)
});

// A request with a size is resized, kept and answered here, before it reaches any endpoint.
app.UseImageSharp();

// One without: the image as it is, a library cover straight off the disk, a remote one fetched first.
app.MapGet(Images.Route, async (string? url, Images images, HttpResponse response, CancellationToken ct) =>
{
    if (images.Parse(url) is null)
        return Results.BadRequest("url is a cover:// name or an https URL on an allowed host.");

    if (await images.ResolveAsync(url, ct) is not { } path) return Results.NotFound();

    response.Headers.CacheControl = Forever;
    return Results.File(path, Remote.ContentType(path), enableRangeProcessing: true);
});

// Where the library pod puts a cover it extracted, and the only route that writes. Local only, in three
// layers, like Stih's /register: it is not under /Audio, so no nginx location reaches it; the published
// port is bound to 127.0.0.1; and with ADMIN_TOKEN set it wants the header Oko uses.
app.MapPost("/store", async (HttpRequest request, CancellationToken ct) =>
{
    if (!AdminApi.Authorized(request, app.Configuration["ADMIN_TOKEN"])) return Results.Unauthorized();

    using var body = new MemoryStream();
    await request.Body.CopyToAsync(body, ct);

    var name = covers.Store(body.ToArray());
    if (name is null) return Results.BadRequest("Not a JPEG or a PNG.");

    Log.Debug("Stored {Name}", name);
    return Results.Text(name);
});

app.Run();
return 0;

static object Measure(string directory)
{
    if (!Directory.Exists(directory)) return new { files = 0, bytes = 0L };

    var files = new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories).ToList();
    return new { files = files.Count, bytes = files.Sum(file => file.Length) };
}
