using System.Globalization;
using System.Runtime.CompilerServices;
using Gaida.Admin;
using Gaida.Core.Platforms;
using Gaida.Core.Streams;
using Gaida.Core.Utils;
using Gaida.Platforms.YouTube;
using Gaida.Pods.YouTube;
using Serilog;
using ILogger = Serilog.ILogger;
using YouTubePlatform = Gaida.Platforms.YouTube.YouTube;

if (args.Contains("--self-check"))
{
    ClassifySelfCheck.Run();
    return;
}

Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .WriteTo.Console(formatProvider: CultureInfo.CurrentCulture)
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSerilog();
builder.Services.AddSingleton(Log.Logger);

var app = builder.Build();

// One instance for the process lifetime: this pod owns exactly one platform. Initialize() opens the
// search cache (YouTubeSearchProviderCached.Initialize) and orders the content downloaders
// by priority (GetterLocalCache 99 > GetterYouTubeExplode 40 > GetterYtDlp 20).
// An empty value is the same as an unset one, as it was when these travelled as environment variables.
var youTube = new YouTubePlatform(Log.Logger,
    new YouTubeOptions(Setting("YOUTUBE_CACHE_DB"), Setting("YOUTUBE_CACHE")));
youTube.Initialize();

// No state an operator edits, only a search cache and a webm cache an operator may page through and prune.
// No-op without ADMIN_TOKEN. See ADMIN_PLAN.md and CACHE_BROWSER_PLAN.md.
var admin = app.MapAdmin(() =>
{
    var (results, downloads, downloadBytes) = youTube.Cache.Count();
    return new { service = "gaida-youtube", results, downloads, downloadBytes };
});

// Every cached result with its webm beside it, then the webms nothing describes. A GET, so Oko forwards
// it through its read proxy and leaves it out of the audit log.
admin?.MapGet("/tracks", async Task<IResult> (string? q, int? offset, int? take, long? top, bool? downloaded,
    CancellationToken ct) => Results.Ok(await youTube.Cache.PageAsync(q, offset ?? 0, take ?? 100, top,
    downloaded ?? false, ct)));

// The writes: a cached result, gone, or a webm, gone. A downloaded one is evicted first, or it would drop
// out of Oko's table while its webm stayed on disk.
admin?.MapPost("/forget", async Task<IResult> (string? id, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(id)) return Results.BadRequest(new { error = "A YouTube video ID is required." });
    if (await youTube.Cache.IsDownloadedAsync(id, ct))
        return Results.Conflict(new { error = "That video is downloaded. Evict it first." });

    return Results.Ok(new { forgotten = await youTube.Cache.ForgetAsync(id, ct) ? 1 : 0 });
});

admin?.MapPost("/evict", async Task<IResult> (string? id, CancellationToken ct) =>
    string.IsNullOrWhiteSpace(id)
        ? Results.BadRequest(new { error = "A YouTube video ID is required." })
        : Results.Ok(new { evicted = await youTube.Cache.EvictAsync(id, ct) ? 1 : 0 }));

app.MapGet("/classify", (string? query) =>
{
    var result = Classify.Parse(query);
    return result.Status switch
    {
        200 => Results.Ok(new ClassifyDto(result.Kind, result.Id, null)),
        400 => Results.Json(new ClassifyDto(null, null, result.Error), statusCode: 400),
        _ => Results.NotFound()
    };
});

app.MapGet("/resolve", async (string? id, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(id)) return Results.NotFound();

    var result = await youTube.GetByIdAsync(PlatformId.StripPrefix(id), ct);
    var dto = result is null ? null : ResultMapper.Map(result);
    return dto is null ? Results.NotFound() : Results.Ok(dto);
});

// The routes hand back the sequence itself: ASP.NET serialises an IAsyncEnumerable element by element
// and flushes as it goes, so a caller reading the body incrementally sees each track without waiting for
// the last one. A playlist is the case that pays for it: YouTubeExplode fetches it a batch at a time.
app.MapGet("/search",
    IResult (string? q, ILogger logger, CancellationToken ct) => string.IsNullOrWhiteSpace(q)
        ? Results.Ok(Array.Empty<ResultDto>())
        : Results.Ok(Mapped(youTube.SearchKeywords(q, ct), logger, ct)));

app.MapGet("/playlist",
    IResult (string? url, ILogger logger, CancellationToken ct) => string.IsNullOrWhiteSpace(url)
        ? Results.Ok(Array.Empty<ResultDto>())
        : Results.Ok(Mapped(youTube.SearchPlaylist(url, ct), logger, ct)));

app.MapGet("/random",
    IResult (int count, ILogger logger, CancellationToken ct) => count < 1
        ? Results.Ok(Array.Empty<ResultDto>())
        : Results.Ok(Mapped(youTube.GetRandomResults(count, ct), logger, ct)));

app.MapGet("/content", async (string? id, HttpContext http, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(id)) return Results.NotFound();

    var pureId = PlatformId.StripPrefix(id);
    var result = await youTube.GetByIdAsync(pureId, ct);
    if (result is null) return Results.NotFound();

    var spreader = await result.GetContentDataAsync(ct);
    if (spreader is null) return Results.NotFound();

    http.Response.ContentType = "audio/webm";
    http.Response.Headers.Append("Content-Disposition", $"attachment; filename={pureId}.webm");

    await spreader.DrainToAsync(http.Response.Body, ct);
    return Results.Empty;
});

app.Run();
return;

// Guarded exactly where Collect had it: a provider that throws halfway ends the array cleanly.
string? Setting(string key)
{
    return builder.Configuration[key] is { Length: > 0 } value ? value : null;
}

static async IAsyncEnumerable<ResultDto> Mapped(IAsyncEnumerable<PlatformResult> source, ILogger logger,
    [EnumeratorCancellation] CancellationToken ct)
{
    await foreach (var result in source.Guarded(logger, nameof(YouTubePlatform), ct))
        if (ResultMapper.Map(result) is { } dto)
            yield return dto;
}

