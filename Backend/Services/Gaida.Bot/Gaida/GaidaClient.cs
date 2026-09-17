using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Gaida.Bot.Gaida;

/// <summary>
/// The bot's whole view of Gaida: five calls against the running instance. Nothing is cached here —
/// replays, loops and reconnects are fresh requests, and coalescing is the API's and Dunav's job.
/// </summary>
[SuppressMessage("Microsoft.Design", "CA1001:TypesThatOwnDisposableFieldsShouldBeDisposable",
    Justification = "One instance is shared by every account and lives as long as the process; there is no " +
                    "point in the run at which disposing it would be correct.")]
public sealed class GaidaClient(ILogger logger, string? baseUrl = null)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    // No total timeout: /Audio/Download streams for the length of a track, and HttpClient's
    // default 100s would cut every song off mid-play.
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public string BaseUrl { get; } = (baseUrl ?? Environment.GetEnvironmentVariable("GAIDA_API_BASE_URL")
        ?? "http://localhost:5340").TrimEnd('/');

    /// <summary>
    /// What a pasted value is: a local ID, a video, a playlist, or ordinary text to search. This is
    /// what tells a playlist (queue all of it) from a search term (queue one track).
    /// </summary>
    public async Task<QueryResolution?> ResolveAsync(string query, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _http.GetAsync(
                $"{BaseUrl}/Audio/FindQueryType?query={Uri.EscapeDataString(query)}", cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonSerializer.DeserializeAsync<QueryResolution>(stream, Json, cancellationToken);
        }
        catch (Exception e)
        {
            logger.Warning(e, "Resolving {Query} failed", query);
            return null;
        }
    }

    /// <summary>
    /// Search results as they arrive. The endpoint streams its array element by element, so a
    /// playlist fills the queue while it resolves instead of after the last track.
    /// </summary>
    public async IAsyncEnumerable<Track> SearchAsync(string query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await _http.GetAsync($"{BaseUrl}/Audio/Search?query={Uri.EscapeDataString(query)}",
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            logger.Warning("Search for {Query} answered {Status}", query, response.StatusCode);
            yield break;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await foreach (var track in JsonSerializer.DeserializeAsyncEnumerable<Track>(stream, Json, cancellationToken))
        {
            if (track is not null) yield return track;
        }
    }

    /// <summary>Opens the Ogg/Opus body for a track. The caller disposes the response.</summary>
    public async Task<HttpResponseMessage?> OpenAudioAsync(string id, int bitrate,
        CancellationToken cancellationToken = default)
    {
        var response = await _http.GetAsync(
            $"{BaseUrl}/Audio/Download/Opus/{bitrate}?id={Uri.EscapeDataString(id)}",
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (response.IsSuccessStatusCode) return response;

        logger.Warning("Opening audio for {ID} answered {Status}", id, response.StatusCode);
        response.Dispose();
        return null;
    }

    /// <summary>Warms the encode for a track we are about to want. Failure is not interesting.</summary>
    public async Task PreloadAsync(string id, int bitrate)
    {
        try
        {
            using var response = await _http.GetAsync(
                $"{BaseUrl}/Audio/Preload/Opus/{bitrate}?id={Uri.EscapeDataString(id)}");
            logger.Debug("Preloading {ID} answered {Status}", id, response.StatusCode);
        }
        catch (Exception e)
        {
            logger.Debug(e, "Preloading {ID} failed", id);
        }
    }

    /// <summary><c>null</c> when there are none, which is the ordinary answer for a great many tracks.</summary>
    public async Task<LyricsResult?> LyricsAsync(string id, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _http.GetAsync(
                $"{BaseUrl}/Audio/Lyrics/Get?id={Uri.EscapeDataString(id)}", cancellationToken);
            if (!response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NoContent) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonSerializer.DeserializeAsync<LyricsResult>(stream, Json, cancellationToken);
        }
        catch (Exception e)
        {
            logger.Warning(e, "Fetching lyrics for {ID} failed", id);
            return null;
        }
    }
}
