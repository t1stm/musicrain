namespace Gaida.API;

/// <summary>
///     Gaida.API's settings, under the keys it has always read. The pod list stays a configuration section that
///     <see cref="ManagerService" /> walks in key order.
/// </summary>
public sealed class GaidaApiOptions
{
    /// <summary>The host content URLs are built on outside Development. Unset or not absolute: the request's own.</summary>
    public string? PublicApiBaseUrl { get; set; }

    /// <summary>YouTube's share of a random mix when the request does not name one.</summary>
    [ConfigurationKeyName("Random:Shares:youtube")]
    public double RandomYouTubeShare { get; set; } = 0.4;

    /// <summary>Name lookups in flight at once when a playlist of names is resolved. At least one runs.</summary>
    [ConfigurationKeyName("Resolve:Concurrency")]
    public int ResolveConcurrency { get; set; } = 4;
}
