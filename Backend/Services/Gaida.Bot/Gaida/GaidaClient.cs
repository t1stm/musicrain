using System.Runtime.CompilerServices;
using System.Text.Json;
using Serilog;

namespace Gaida.Bot.Gaida;

/// <summary>
/// The bot's whole view of Gaida: five calls against the running instance. Nothing is cached here —
/// replays, loops and reconnects are fresh requests, and coalescing is the API's and Dunav's job.
/// </summary>
public sealed class GaidaClient
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient http;
    private readonly ILogger logger;

    public GaidaClient(ILogger logger, string? baseUrl = null)
    {
        this.logger = logger;
        this.BaseUrl = (baseUrl ?? Environment.GetEnvironmentVariable("GAIDA_API_BASE_URL")
            ?? "http://localhost:5340").TrimEnd('/');

        // No total timeout: /Audio/Download streams for the length of a track, and HttpClient's
        // default 100s would cut every song off mid-play.
        this.http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    public string BaseUrl { get; }

    /// <summary>
    /// What a pasted value is: a local ID, a video, a playlist, or ordinary text to search. This is
    /// what tells a playlist (queue all of it) from a search term (queue one track).
    /// </summary>
    public async Task<QueryResolution?> ResolveAsync(string query, CancellationToken ct = default)
    {
        try
        {
            var response = await this.http.GetAsync(
                $"{this.BaseUrl}/Audio/FindQueryType?query={Uri.EscapeDataString(query)}", ct);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await JsonSerializer.DeserializeAsync<QueryResolution>(stream, Json, ct);
        }
        catch (Exception e)
        {
            this.logger.Warning(e, "Resolving {Query} failed", query);
            return null;
        }
    }

    /// <summary>
    /// Search results as they arrive. The endpoint streams its array element by element, so a
    /// playlist fills the queue while it resolves instead of after the last track.
    /// </summary>
    public async IAsyncEnumerable<Track> SearchAsync(string query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var response = await this.http.GetAsync($"{this.BaseUrl}/Audio/Search?query={Uri.EscapeDataString(query)}",
            HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode)
        {
            this.logger.Warning("Search for {Query} answered {Status}", query, response.StatusCode);
            yield break;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await foreach (var track in JsonSerializer.DeserializeAsyncEnumerable<Track>(stream, Json, ct))
        {
            if (track is not null) yield return track;
        }
    }

    /// <summary>Opens the Ogg/Opus body for a track. The caller disposes the response.</summary>
    public async Task<HttpResponseMessage?> OpenAudioAsync(string id, int bitrate, CancellationToken ct = default)
    {
        var response = await this.http.GetAsync(
            $"{this.BaseUrl}/Audio/Download/Opus/{bitrate}?id={Uri.EscapeDataString(id)}",
            HttpCompletionOption.ResponseHeadersRead, ct);

        if (response.IsSuccessStatusCode) return response;

        this.logger.Warning("Opening audio for {Id} answered {Status}", id, response.StatusCode);
        response.Dispose();
        return null;
    }

    /// <summary>Warms the encode for a track we are about to want. Failure is not interesting.</summary>
    public async Task PreloadAsync(string id, int bitrate)
    {
        try
        {
            using var response = await this.http.GetAsync(
                $"{this.BaseUrl}/Audio/Preload/Opus/{bitrate}?id={Uri.EscapeDataString(id)}");
            this.logger.Debug("Preloading {Id} answered {Status}", id, response.StatusCode);
        }
        catch (Exception e)
        {
            this.logger.Debug(e, "Preloading {Id} failed", id);
        }
    }

    /// <summary><c>null</c> when there are none, which is the ordinary answer for a great many tracks.</summary>
    public async Task<LyricsResult?> LyricsAsync(string id, CancellationToken ct = default)
    {
        try
        {
            var response = await this.http.GetAsync(
                $"{this.BaseUrl}/Audio/Lyrics/Get?id={Uri.EscapeDataString(id)}", ct);
            if (!response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NoContent) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await JsonSerializer.DeserializeAsync<LyricsResult>(stream, Json, ct);
        }
        catch (Exception e)
        {
            this.logger.Warning(e, "Fetching lyrics for {Id} failed", id);
            return null;
        }
    }
}
