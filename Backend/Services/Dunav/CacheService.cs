using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Timers;
using Gaida.Core.Streams;
using JetBrains.Annotations;
using ILogger = Serilog.ILogger;
using Timer = System.Timers.Timer;

namespace Dunav;

/// <summary>
///     One upstream fetch per cache key, however many clients race for it -- coalescing lifted from
///     <c>Gaida.API/ManagerService.cs</c>'s <c>GetOrStartEncoderAsync</c>, adapted from
///     <c>Func&lt;FFmpegEncoder,...&gt;</c> to fetch-upstream-into-a-file. Also owns expiry, the byte-ceiling
///     LRU eviction the old code never had, and the on-disk bodies themselves.
/// </summary>
public class CacheService
{
    private readonly ConcurrentDictionary<string, Lazy<Task<CacheEntry?>>> _cachedEntries = new();
    private readonly ConcurrentDictionary<string, DateTime> _expireTimes = new();
    private readonly string _cacheDir;
    private readonly HttpClient _http;
    private readonly long _maxBytes;
    private readonly TimeSpan _retention;
    private readonly Timer _sweepTimer;

    public CacheService(HttpClient http, ILogger logger, IConfiguration configuration)
    {
        _http = http;
        Logger = logger;
        _retention = TimeSpan.FromMinutes(configuration.GetValue("Dunav:RetentionMinutes", 45));

        // A disk budget, not a memory one. Bodies live in CacheDir; what this bounds is how much of the
        // filesystem they may occupy, so it is sized against free disk rather than the pod's mem_limit.
        _maxBytes = configuration.GetValue("Dunav:MaxBytes", 20L * 1024 * 1024 * 1024);

        // Deliberately NOT a tmpfs mount: tmpfs pages are charged to the container's memory cgroup and
        // cannot be reclaimed, which is the OOM this whole design exists to avoid. Ordinary files on an
        // ordinary filesystem give reclaimable page cache instead. See DUNAV_SPILL_PLAN.md.
        _cacheDir = configuration.GetValue("Dunav:CacheDir", "/tmp/dunav");
        Directory.CreateDirectory(_cacheDir);

        // Wipe on boot. _cachedEntries starts empty, so nothing can reference a leftover file, and this is
        // what lets the writer use the final filename directly -- no .part suffix, no atomic rename, no
        // startup reconciliation to decide whether a stray file is complete.
        foreach (var stale in Directory.EnumerateFiles(_cacheDir))
            try
            {
                File.Delete(stale);
            }
            catch (IOException exception)
            {
                Logger.Warning(exception, "Could not remove stale cache file {File}", stale);
            }

        _sweepTimer = new Timer(TimeSpan.FromMinutes(1)) { Enabled = true };
        _sweepTimer.Elapsed += Sweep;
    }

    private ILogger Logger { get; }

    /// <summary>
    ///     Hex so the key is valid as a filename -- which is exactly what it now is: every key names a file
    ///     under <c>Dunav:CacheDir</c>.
    /// </summary>
    // ponytail: only SHA-256 hex used here, no truncation/base64 tradeoffs considered -- id strings are
    // short (a video ID or a local path), so collision risk and key length are both non-issues.
    private static string HashId(string id)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
    }

    public static string RawKey(string id)
    {
        return $"raw-{HashId(id)}";
    }

    public static string EncodedKey(string codec, int bitrate, string id)
    {
        return $"{codec}-{bitrate}-{HashId(id)}";
    }

    /// <summary>
    ///     Starts the fetch for <paramref name="key" /> at most once, however many requests race for it: the
    ///     losers await the winner's task instead of issuing a second upstream request for the same bytes.
    /// </summary>
    /// <param name="key">Identifies the entry; see <see cref="EncodedKey" />.</param>
    /// <param name="start">Feeds the entry's spreader from upstream. Returns <c>false</c> when the fetch could not be started.</param>
    /// <param name="started">
    ///     <c>true</c> for the single caller whose call actually started the fetch; every racer that found the
    ///     key already there gets <c>false</c>. Preload answers 202 or 200 off this.
    /// </param>
    /// <param name="label">Human-readable description of the entry, for <see cref="Snapshot" />. Never used for lookup.</param>
    public Task<CacheEntry?> GetOrStartAsync(string key, Func<CacheEntry, Task<bool>> start, out bool started,
        string? label = null)
    {
        // Built before the add so that the add is the only race: GetOrAdd's factory overload may run for more
        // than one caller, and then two of them would each believe they started the fetch. A Lazy that loses
        // is discarded before it ever calls upstream.
        var lazy = new Lazy<Task<CacheEntry?>>(async () =>
        {
            var entry = new CacheEntry
            {
                // Named for its key and kept until evicted, rather than a self-deleting scratch file.
                Body = new StreamSpreader(Path.Combine(_cacheDir, key), false),
                Label = label
            };
            if (await start(entry)) return entry;

            // A failed start must not stay cached, or every later request inherits the failure.
            Forget(key);
            Delete(entry);
            return null;
        }, LazyThreadSafetyMode.ExecutionAndPublication);

        var cached = _cachedEntries.GetOrAdd(key, lazy);
        started = ReferenceEquals(cached, lazy);

        ExpireIn(key);
        return cached.Value;
    }

    /// <summary>Whether a fetch is already running or finished for <paramref name="key" />, refreshing its expiry.</summary>
    public bool Has(string key)
    {
        if (!_cachedEntries.TryGetValue(key, out var lazy)) return false;

        ExpireIn(key);
        _ = lazy.Value; // the racer that finds a cold entry must still start it, as the out parameter used to
        return true;
    }

    /// <summary>
    ///     Drops <paramref name="key" /> from the index without touching its file. Used when a reader finds
    ///     the file already unlinked -- the entry has outlived its body and must not be handed out again.
    /// </summary>
    public void Forget(string key)
    {
        _cachedEntries.TryRemove(key, out _);
        _expireTimes.TryRemove(key, out _);
    }

    private void ExpireIn(string key)
    {
        _expireTimes[key] = DateTime.UtcNow.Add(_retention);
    }

    /// <summary>
    ///     Fetches <paramref name="upstreamPath" /> (relative to <c>Gaida:Url</c>) into <paramref name="entry" />'s
    ///     spreader. Returns once headers are read and the body copy is subscribed -- not once the body is
    ///     finished -- so callers get progressive streaming the same way <c>FFmpegEncoder.Convert</c> does.
    /// </summary>
    public async Task<bool> FetchAsync(CacheEntry entry, string upstreamPath, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(upstreamPath, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch (Exception exception)
        {
            Logger.Warning(exception, "Upstream fetch failed for {Path}", upstreamPath);
            return false;
        }

        if (!response.IsSuccessStatusCode)
        {
            Logger.Warning("Upstream returned {Status} for {Path}", response.StatusCode, upstreamPath);
            response.Dispose();
            return false;
        }

        entry.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
        entry.ContentDisposition = response.Content.Headers.ContentDisposition?.ToString();
        entry.ETag = response.Headers.ETag?.Tag;

        var upstreamStream = await response.Content.ReadAsStreamAsync(cancellationToken);

        // Fire-and-forget on purpose, and deliberately not tied to the triggering request's cancellation
        // token: the fetch outlives the request that started it, same as GetContentDataAsync being called
        // with CancellationToken.None in Gaida.API's StartEncode.
        _ = PumpAsync(response, upstreamStream, entry, upstreamPath);
        return true;
    }

    /// <summary>
    ///     Copies the upstream body into the entry's spreader, which flushes and publishes as it goes so
    ///     followers see bytes as they land.
    /// </summary>
    private async Task PumpAsync(HttpResponseMessage response, Stream source, CacheEntry entry, string upstreamPath)
    {
        var failed = false;
        try
        {
            await source.CopyToAsync(entry.Body);
        }
        catch (Exception exception)
        {
            // A body that died halfway is on disk and looks complete once closed. Serving it would hand
            // clients truncated audio with a confident Content-Length, so drop the key: the next request
            // re-fetches instead of inheriting the stump. Readers already attached still drain what
            // arrived -- their handle outlives the unlink.
            failed = true;
            Logger.Warning(exception, "Upstream body failed mid-transfer for {Path}", upstreamPath);
        }
        finally
        {
            await entry.Body.CloseAsync();
            response.Dispose();

            if (failed)
            {
                Forget(KeyOf(entry));
                Delete(entry);
            }
        }
    }

    private string KeyOf(CacheEntry entry)
    {
        return Path.GetFileName(entry.Body.Path);
    }

    private void Delete(CacheEntry entry)
    {
        try
        {
            entry.Body.Dispose();
            File.Delete(entry.Body.Path);
        }
        catch (IOException exception)
        {
            Logger.Warning(exception, "Could not delete cache file {File}", entry.Body.Path);
        }
    }

    private void Sweep(object? sender, ElapsedEventArgs elapsedEventArgs)
    {
        var now = DateTime.UtcNow;

        foreach (var (key, expire) in _expireTimes)
        {
            if (expire > now) continue;
            Evict(key, "expired");
        }

        EvictOverCeiling();
    }

    /// <summary>
    ///     LRU eviction once total cached bytes cross <c>Dunav:MaxBytes</c>.
    /// </summary>
    /// <remarks>
    ///     Only finished entries are eligible, so a burst of concurrent cold starts can briefly hold the
    ///     total above the ceiling. That was a crash when the bodies were on the heap; against a disk budget
    ///     it is just a temporary overshoot. Note also that an unlinked file still occupies space until the
    ///     last reader closes its handle, so <c>df</c> can lag this figure by whatever is currently
    ///     streaming -- do not tune the budget to the last gigabyte.
    /// </remarks>
    private void EvictOverCeiling()
    {
        if (_maxBytes <= 0) return;

        var live = _cachedEntries
            .Where(kv => kv.Value is
                { IsValueCreated: true, Value: { IsCompletedSuccessfully: true, Result: not null } })
            .Select(kv => (kv.Key, Entry: kv.Value.Value.Result!,
                Expire: _expireTimes.GetValueOrDefault(kv.Key, DateTime.MinValue)))
            .ToList();

        var total = live.Sum(x => x.Entry.Body.Length);
        if (total <= _maxBytes) return;

        // ExpireTimes doubles as a recency signal: every TryGet/GetOrStart refreshes it to now+Retention,
        // so the smallest expiry is also the least recently used. Only finished entries are eligible, so
        // eviction never unlinks the file out from under a fetch still writing to it.
        foreach (var (key, entry, _) in live.Where(x => x.Entry.Body.Closed).OrderBy(x => x.Expire))
        {
            if (total <= _maxBytes) break;
            if (!Evict(key, "over byte ceiling")) continue;
            total -= entry.Body.Length;
        }
    }

    public bool Evict(string key, string reason)
    {
        _expireTimes.TryRemove(key, out _);
        if (!_cachedEntries.TryRemove(key, out var lazy)) return false;

        Logger.Information("Evicting cache entry {Key} ({Reason})", key, reason);

        // Unlink, do not wait. On Linux the directory entry goes immediately but the inode survives until
        // the last open handle closes, so responses already streaming finish off their own handle and the
        // space comes back when they do. A reader that has not opened yet gets FileNotFoundException, which
        // AudioController turns into a retriable 503.
        if (lazy is { IsValueCreated: true, Value: { IsCompletedSuccessfully: true, Result: { } entry } })
            Delete(entry);
        return true;
    }

    /// <summary>
    ///     What the cache holds right now, for the admin panel. Read straight off the live dictionaries and
    ///     never cached: they are the only copy, and a second one would only ever be a staler one.
    /// </summary>
    public object Snapshot()
    {
        var entries = _cachedEntries
            .Select(kv =>
            {
                // A fetch whose task has not completed yet has no observable CacheEntry, so it shows as
                // pending with no size or label. That window is the few hundred ms before upstream headers
                // land, so it is nearly always empty.
                // ponytail: no subscriber count -- StreamSpreader does not track readers, and adding a
                // counter to it touches a primitive the pods depend on. Add it there if "who is streaming
                // this" turns out to be a question anyone actually asks.
                var entry = kv.Value is { IsValueCreated: true, Value.IsCompletedSuccessfully: true }
                    ? kv.Value.Value.Result
                    : null;

                return new CacheEntrySnapshot(
                    kv.Key,
                    entry?.Label,
                    entry?.Body.Length ?? 0,
                    entry is null ? "pending" : entry.Body.Closed ? "complete" : "downloading",
                    entry?.ContentType,
                    _expireTimes.TryGetValue(kv.Key, out var expires) ? expires : null);
            })
            .OrderByDescending(entry => entry.Bytes)
            .ToList();

        return new
        {
            count = entries.Count,
            totalBytes = entries.Sum(entry => entry.Bytes),
            maxBytes = _maxBytes,
            retentionMinutes = _retention.TotalMinutes,
            cacheDir = _cacheDir,
            entries
        };
    }

    /// <summary>Evicts everything. Returns how many entries went.</summary>
    public int EvictAll()
    {
        return _cachedEntries.Keys.Count(key => Evict(key, "admin evict-all"));
    }
}

/// <summary>One row of <see cref="CacheService.Snapshot" />.</summary>
/// <param name="Key">The on-disk filename, and what <c>POST /Admin/evict</c> takes.</param>
/// <param name="Label">The codec/bitrate/id this was cached for, or <c>null</c> while still pending.</param>
/// <param name="State">One of <c>pending</c>, <c>downloading</c> or <c>complete</c>.</param>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record CacheEntrySnapshot(
    string Key,
    string? Label,
    long Bytes,
    string State,
    string? ContentType,
    DateTime? ExpiresUtc);
