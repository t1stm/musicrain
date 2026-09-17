using System.Text.Json;
using Gaida.Core;
using Gaida.Core.Platforms;
using Gaida.Platforms.MusicDatabase;
using Gaida.Platforms.YouTube;
using Serilog;

var logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateLogger();

var audioManager = new AudioManager(logger);

audioManager.RegisterPlatform(new YouTube(logger));
audioManager.RegisterPlatform(new MusicDatabase(logger));

// https://www.youtube.com/watch?v=dQw4w9WgXcQ
var result = await audioManager.SearchId("yt://dQw4w9WgXcQ");
if (result is null)
{
    logger.Error("Search: not found");
    return;
}

logger.Information("Search: OK, Result: {Result}",
    JsonSerializer.Serialize(result, CustomSerializer.SerializerOptions));

var streamSpreader = await result.GetContentDataAsync();
if (streamSpreader is null)
{
    logger.Error("Download: Error");
    return;
}

await using var stream = File.Open("test", FileMode.Create);
await using var reader = streamSpreader.OpenRead();
await reader.CopyToAsync(stream);

logger.Information("Total: {Total}", stream.Length);