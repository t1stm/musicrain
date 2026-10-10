namespace Gaida.Platforms.YouTube;

/// <summary>Where the YouTube platform keeps what it has seen and what it has downloaded.</summary>
/// <param name="CacheDatabase">
///     The search cache, <c>YOUTUBE_CACHE_DB</c>. That variable named the JSON file before, so whatever
///     extension it carries, the database is the <c>.db</c> beside it and the JSON is the legacy import.
/// </param>
/// <param name="AudioCache">The webm cache, <c>YOUTUBE_CACHE</c>.</param>
public sealed record YouTubeOptions(string? CacheDatabase = null, string? AudioCache = null)
{
    public string CacheDatabasePath => CacheDatabase ?? "./cache/YouTube.db";

    public string AudioCacheDirectory => AudioCache ?? "./YouTube Cache";
}
