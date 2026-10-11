namespace Dunav;

/// <summary>Dunav's settings, under the keys it has always read.</summary>
public sealed class DunavOptions
{
    /// <summary>The Gaida.API every encode is fetched from.</summary>
    [ConfigurationKeyName("Gaida:Url")]
    public string GaidaUrl { get; set; } = "http://localhost:8080";

    /// <summary>How long an unread body is kept.</summary>
    [ConfigurationKeyName("Dunav:RetentionMinutes")]
    public int RetentionMinutes { get; set; } = 45;

    /// <summary>
    ///     A disk budget, not a memory one. Bodies live in <see cref="CacheDir" />; what this bounds is how much
    ///     of the filesystem they may occupy, so it is sized against free disk rather than the pod's mem_limit.
    /// </summary>
    [ConfigurationKeyName("Dunav:MaxBytes")]
    public long MaxBytes { get; set; } = 20L * 1024 * 1024 * 1024;

    /// <summary>
    ///     Deliberately NOT a tmpfs mount: tmpfs pages are charged to the container's memory cgroup and cannot be
    ///     reclaimed, which is the OOM this whole design exists to avoid. Ordinary files on an ordinary
    ///     filesystem give reclaimable page cache instead. See DUNAV_SPILL_PLAN.md.
    /// </summary>
    [ConfigurationKeyName("Dunav:CacheDir")]
    public string CacheDir { get; set; } = "/tmp/dunav";
}
