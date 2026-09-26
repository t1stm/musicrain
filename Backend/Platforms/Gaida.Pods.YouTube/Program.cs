using Gaida.Admin;
using System.Runtime.CompilerServices;
using Gaida.Core.Platforms;
using Gaida.Core.Streams;
using Gaida.Core.Utils;
using Gaida.Pods.YouTube;
using Serilog;
using YouTubePlatform = Gaida.Platforms.YouTube.YouTube;

if (args.Contains("--self-check"))
{
    ClassifySelfCheck.Run();
    return;
}

Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);

// The platform layer reads these as process environment variables; configuration supplies the defaults.
// See Gaida.API/Program.cs:18 for the same pattern.
foreach (var key in (string[])["YOUTUBE_CACHE_DB", "YOUTUBE_CACHE"])
    Environment.SetEnvironmentVariable(key, builder.Configuration[key]);

builder.Services.AddSerilog();

var app = builder.Build();

// The request ring is this pod's whole admin payload -- it holds no state an operator edits.
// No-op without ADMIN_TOKEN. See ADMIN_PLAN.md.
app.MapAdmin(() => new { service = "gaida-youtube" });

// One instance for the process lifetime: this pod owns exactly one platform. Initialize() loads the
// Info.json search cache (YouTubeSearchProviderCached.Initialize) and orders the content downloaders
// by priority (GetterLocalCache 99 > GetterYouTubeExplode 40 > GetterYtDlp 20).
var youTube = new YouTubePlatform(Log.Logger);
youTube.Initialize();

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

    var result = await youTube.GetByIdAsync(StripScheme(id), ct);
    var dto = result is null ? null : ResultMapper.Map(result);
    return dto is null ? Results.NotFound() : Results.Ok(dto);
});

// The routes hand back the sequence itself: ASP.NET serialises an IAsyncEnumerable element by element
// and flushes as it goes, so a caller reading the body incrementally sees each track without waiting for
// the last one. A playlist is the case that pays for it: YouTubeExplode fetches it a batch at a time.
app.MapGet("/search",
    IResult (string? q, CancellationToken ct) => string.IsNullOrWhiteSpace(q)
        ? Results.Ok(Array.Empty<ResultDto>())
        : Results.Ok(Mapped(youTube.SearchKeywords(q, ct), ct)));

app.MapGet("/playlist",
    IResult (string? url, CancellationToken ct) => string.IsNullOrWhiteSpace(url)
        ? Results.Ok(Array.Empty<ResultDto>())
        : Results.Ok(Mapped(youTube.SearchPlaylist(url, ct), ct)));

app.MapGet("/random",
    IResult (int count, CancellationToken ct) => count < 1
        ? Results.Ok(Array.Empty<ResultDto>())
        : Results.Ok(Mapped(youTube.GetRandomResults(count, ct), ct)));

app.MapGet("/content", async (string? id, HttpContext http, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(id)) return Results.NotFound();

    var pureId = StripScheme(id);
    var result = await youTube.GetByIdAsync(pureId, ct);
    if (result is null) return Results.NotFound();

    var spreader = await result.GetContentDataAsync(ct);
    if (spreader is null) return Results.NotFound();

    http.Response.ContentType = "audio/webm";
    http.Response.Headers.Append("Content-Disposition", $"attachment; filename={pureId}.webm");

    await PumpToResponse(spreader, http.Response, ct);
    return Results.Empty;
});

app.Run();
return;

static string StripScheme(string id)
{
    var separator = id.IndexOf("://", StringComparison.Ordinal);
    return separator < 0 ? id : id[(separator + 3)..];
}

// Guarded exactly where Collect had it: a provider that throws halfway ends the array cleanly.
static async IAsyncEnumerable<ResultDto> Mapped(IAsyncEnumerable<PlatformResult> source,
    [EnumeratorCancellation] CancellationToken ct)
{
    await foreach (var result in source.Guarded(Log.Logger, nameof(YouTubePlatform), ct))
        if (ResultMapper.Map(result) is { } dto)
            yield return dto;
}

// Pumps a stream spreader into the response body until the source closes or the client leaves. The
// download may still be writing while this drains it, so the reader follows the body as it grows
// rather than stopping at whatever had arrived when it opened.
static async Task PumpToResponse(StreamSpreader streamSpreader, HttpResponse response,
    CancellationToken cancellationToken)
{
    await using var reader = streamSpreader.OpenRead();
    await reader.CopyToAsync(response.Body, cancellationToken);
}
