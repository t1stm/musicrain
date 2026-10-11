// Standalone proof of the file-backed fan-out in DUNAV_SPILL_PLAN.md.
//
//   dotnet run --project scripts/FollowProbe
//
// Exit code 0 = every scenario passed. The CacheEntry and the reader loop below are copied
// verbatim from sections 1 and 3 of the plan -- if this file works, that design works.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace FollowProbe;

/// <summary>Section 1 of the plan, verbatim. The whole coordination surface between writer and readers.</summary>
[SuppressMessage("Microsoft.Design", "CA1051:DoNotDeclareVisibleInstanceFields",
    Justification = "Copied verbatim from the plan, and read through Volatile and ref: a property cannot be either.")]
public sealed class CacheEntry
{
    private TaskCompletionSource Signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public required string Path { get; init; }

    /// <summary>Bytes written so far. For the eviction budget only -- readers do not consult it.</summary>
    public long Length;

    /// <summary>Set once the body is complete. A reader that observes this and then reads 0 bytes is done.</summary>
    public volatile bool Closed;

    /// <summary>Completes on the next Publish. Capture it BEFORE testing Closed, or you race a bump away.</summary>
    public Task Changed => Volatile.Read(ref Signal).Task;

    public void Publish(long length, bool closed)
    {
        Volatile.Write(ref Length, length);
        Closed = closed;
        Interlocked.Exchange(ref Signal,
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).SetResult();
    }
}

public static class Program
{
    private const int BufferSize = 64 * 1024;

    // FileShare.ReadWrite lets writer and readers coexist; FileShare.Delete lets eviction unlink
    // while handles are open. Both flags on BOTH sides. This is the load-bearing detail.
    private const FileShare Share = FileShare.ReadWrite | FileShare.Delete;

    private static int Failures;

    public static async Task<int> Main(string[] args)
    {
        // "live" runs only the traced scenario, which is the one worth watching rather than skimming.
        if (!args.Contains("live"))
        {
            await ScenarioA_ConcurrentFollowersAreByteIdentical();
            await ScenarioB_UnlinkMidFlight();
            await ScenarioC_MemoryStaysFlatUnderFanOut();
            await ScenarioD_OneSlowReaderDoesNotHoldBackTheOthers();
        }

        await ScenarioE_LiveTrace();
        await ScenarioF_FiveTracedReaders();

        Console.WriteLine();
        Console.WriteLine(Failures == 0
            ? "ALL SCENARIOS PASSED"
            : $"{Failures} CHECK(S) FAILED");
        return Failures == 0 ? 0 : 1;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // The writer and the reader. Everything below Main is the actual proposed design.
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Section 2. Writes <paramref name="payload" /> in chunks, flushing each one to the OS before
    ///     publishing, because a reader can only see bytes that reached the page cache.
    ///     <paramref name="onProgress" /> is the hook the eviction scenario uses to unlink mid-write.
    /// </summary>
    private static async Task WriteAsync(CacheEntry entry, byte[] payload, int chunk, int delayMs,
        Func<long, Task>? onProgress = null)
    {
        try
        {
            // bufferSize 0 -- no user-space buffering, so FlushAsync is the only thing between
            // a WriteAsync and a reader being able to see those bytes.
            await using var file = new FileStream(entry.Path, FileMode.Create, FileAccess.Write,
                Share, 0, FileOptions.Asynchronous);

            long written = 0;
            for (var offset = 0; offset < payload.Length; offset += chunk)
            {
                var count = Math.Min(chunk, payload.Length - offset);
                await file.WriteAsync(payload.AsMemory(offset, count));
                await file.FlushAsync();
                entry.Publish(written += count, false);

                if (onProgress is not null) await onProgress(written);
                if (delayMs > 0) await Task.Delay(delayMs);
            }
        }
        finally
        {
            entry.Publish(Volatile.Read(ref entry.Length), true);
        }
    }

    /// <summary>
    ///     Section 3, verbatim. Each caller opens its OWN FileStream and follows the file as it grows.
    ///     Cannot truncate: it exits only when a real read returned 0 bytes AND Closed was observed
    ///     before that read, so anything the writer flushed is necessarily consumed first.
    /// </summary>
    private static async Task FollowAsync(CacheEntry entry, Stream destination, int slowByMs = 0,
        Action<int, long>? onRead = null, Action? onWait = null)
    {
        await using var file = new FileStream(entry.Path, FileMode.Open, FileAccess.Read,
            Share, BufferSize, FileOptions.Asynchronous);

        var buffer = new byte[BufferSize];
        while (true)
        {
            var changed = entry.Changed;   // capture BEFORE testing Closed -- the one ordering rule
            var closed = entry.Closed;

            var read = await file.ReadAsync(buffer);   // read() is the truth; Length is never consulted
            if (read > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read));
                onRead?.Invoke(read, file.Position);
                if (slowByMs > 0) await Task.Delay(slowByMs);
                continue;
            }

            if (closed) break;
            onWait?.Invoke();
            await changed;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Scenarios
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Readers joining at different points during a write all reconstruct the payload exactly.</summary>
    private static async Task ScenarioA_ConcurrentFollowersAreByteIdentical()
    {
        Header("A. Concurrent followers are byte-identical",
            "one writer appending, four readers joining at different points");

        var path = TempPath();
        var payload = RandomBytes(3 * 1024 * 1024);
        var entry = new CacheEntry { Path = path };

        // 33 chunks x 15ms, so the write spans roughly half a second and the readers below
        // genuinely start mid-stream rather than after the fact.
        var writer = WriteAsync(entry, payload, 97_000, 15);

        await WaitForFile(path);

        async Task<byte[]> Reader(string label, int joinAtMs)
        {
            await Task.Delay(joinAtMs);
            var seenClosed = entry.Closed;
            var sink = new MemoryStream();
            await FollowAsync(entry, sink);
            Console.WriteLine($"    {label,-22} joined at {joinAtMs,4}ms " +
                              $"(writer {(seenClosed ? "already finished" : "still writing")}) " +
                              $"-> {sink.Length:N0} bytes");
            return sink.ToArray();
        }

        var results = await Task.WhenAll(
            Reader("reader-immediate", 0),
            Reader("reader-early", 80),
            Reader("reader-late", 350),
            Reader("reader-after-close", 900));
        await writer;

        for (var i = 0; i < results.Length; i++)
            Check($"reader {i} matches the payload byte for byte",
                results[i].AsSpan().SequenceEqual(payload));

        Cleanup(path);
    }

    /// <summary>
    ///     Section 5. An already-open handle keeps working after File.Delete; opening by path afterwards
    ///     does not. That second half is the eviction race section 4 has to handle.
    /// </summary>
    private static async Task ScenarioB_UnlinkMidFlight()
    {
        Header("B. Eviction while readers are mid-stream",
            "File.Delete at ~50% -- open handles must survive, later opens must not");

        var path = TempPath();
        var payload = RandomBytes(3 * 1024 * 1024);
        var entry = new CacheEntry { Path = path };
        var unlinked = false;

        var writer = WriteAsync(entry, payload, 97_000, 15, async written =>
        {
            if (unlinked || written < payload.Length / 2) return;
            File.Delete(path);
            unlinked = true;
            Console.WriteLine($"    evicted (unlinked) at {written:N0} of {payload.Length:N0} bytes, still writing");
            await Task.CompletedTask;
        });

        await WaitForFile(path);

        // Opened BEFORE the unlink. Must complete regardless of what happens to the directory entry.
        var sink = new MemoryStream();
        var reader = FollowAsync(entry, sink);

        await writer;
        await reader;

        Check("the file really was unlinked mid-write", unlinked);
        Check("a reader holding an open handle still gets every byte",
            sink.ToArray().AsSpan().SequenceEqual(payload));

        var openAfterUnlinkThrew = false;
        try
        {
            await using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, Share);
        }
        catch (FileNotFoundException)
        {
            openAfterUnlinkThrew = true;
        }

        Check("opening by path after eviction throws FileNotFoundException " +
              "(section 4 must catch this and 503)", openAfterUnlinkThrew);

        Cleanup(path);
    }

    /// <summary>
    ///     The point of the whole plan: managed memory is a function of concurrent readers, not of body
    ///     size. Readers hash as they go instead of buffering, so what is measured is the design's
    ///     footprint and not the harness's.
    /// </summary>
    private static async Task ScenarioC_MemoryStaysFlatUnderFanOut()
    {
        const int readers = 20;
        var payload = RandomBytes(128 * 1024 * 1024);
        var expected = Convert.ToHexStringLower(SHA256.HashData(payload));

        Header("C. Memory stays flat under fan-out",
            $"{payload.Length / 1024 / 1024} MiB body, {readers} concurrent readers");

        var path = TempPath();
        var entry = new CacheEntry { Path = path };

        // Settle first, then measure only the delta this scenario is responsible for. The payload
        // itself is a harness artefact -- Dunav never holds one.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var baseline = GC.GetTotalMemory(true);
        var peak = baseline;

        using var sampling = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!sampling.IsCancellationRequested)
            {
                peak = Math.Max(peak, GC.GetTotalMemory(false));
                await Task.Delay(10);
            }
        });

        var stopwatch = Stopwatch.StartNew();
        var writer = WriteAsync(entry, payload, 1024 * 1024, 0);
        await WaitForFile(path);

        var hashes = await Task.WhenAll(Enumerable.Range(0, readers).Select(async _ =>
        {
            await using var hasher = new HashingStream();
            await FollowAsync(entry, hasher);
            return hasher.Hex();
        }));
        await writer;
        stopwatch.Stop();

        sampling.Cancel();
        await sampler;

        var delta = peak - baseline;
        var served = (long)readers * payload.Length;
        Console.WriteLine($"    served {served / 1024 / 1024:N0} MiB total in {stopwatch.ElapsedMilliseconds:N0}ms");
        Console.WriteLine($"    managed heap grew {delta / 1024.0 / 1024.0:F1} MiB " +
                          $"(one {BufferSize / 1024} KiB buffer per reader = " +
                          $"{readers * BufferSize / 1024.0 / 1024.0:F1} MiB of that)");

        Check($"all {readers} readers hashed identically to the source", hashes.All(h => h == expected));

        // The claim is "flat in body size", so the bar is tied to reader count, not to the 128 MiB.
        // Generous by design: this must fail loudly on a regression, not flap on GC timing.
        var ceiling = readers * BufferSize * 8L;
        Check($"managed heap growth stayed under {ceiling / 1024 / 1024} MiB " +
              "(i.e. scales with readers, not with body size)", delta < ceiling);

        Cleanup(path);
    }

    /// <summary>
    ///     The failure mode that kills the blob-in-memory design: one slow client must not hold bytes
    ///     for everyone else, and must not stop the fast readers finishing.
    /// </summary>
    private static async Task ScenarioD_OneSlowReaderDoesNotHoldBackTheOthers()
    {
        Header("D. A slow reader is isolated",
            "one reader sleeping 25ms per 64 KiB, nine reading at full speed");

        var path = TempPath();
        var payload = RandomBytes(4 * 1024 * 1024);
        var expected = Convert.ToHexStringLower(SHA256.HashData(payload));
        var entry = new CacheEntry { Path = path };

        var writer = WriteAsync(entry, payload, 256 * 1024, 5);
        await WaitForFile(path);

        var stopwatch = Stopwatch.StartNew();
        long fastFinishedAtMs = 0;

        var slow = Task.Run(async () =>
        {
            await using var hasher = new HashingStream();
            await FollowAsync(entry, hasher, 25);
            return (hasher.Hex(), stopwatch.ElapsedMilliseconds);
        });

        var fast = Task.WhenAll(Enumerable.Range(0, 9).Select(async _ =>
        {
            await using var hasher = new HashingStream();
            await FollowAsync(entry, hasher);
            return hasher.Hex();
        }));

        var fastHashes = await fast;
        fastFinishedAtMs = stopwatch.ElapsedMilliseconds;

        var (slowHash, slowFinishedAtMs) = await slow;
        await writer;

        Console.WriteLine($"    nine fast readers done at {fastFinishedAtMs:N0}ms, " +
                          $"slow reader done at {slowFinishedAtMs:N0}ms");

        Check("fast readers finished without waiting for the slow one",
            fastFinishedAtMs < slowFinishedAtMs);
        Check("every fast reader still got the whole body", fastHashes.All(h => h == expected));
        Check("the slow reader eventually got the whole body too", slowHash == expected);

        Cleanup(path);
    }

    /// <summary>
    ///     The same mechanism as scenario A, but narrated: 5 MiB in 64 KiB chunks with a deliberate
    ///     pause between writes, and a reader on a separate thread printing the moment bytes arrive.
    ///     The interleaving is the point -- READ lines land a few milliseconds after their WRITE, and
    ///     every WAIT shows the reader parked on the signal with nothing left to consume.
    /// </summary>
    private static async Task ScenarioE_LiveTrace()
    {
        const int chunk = 64 * 1024;
        const int delayMs = 50;
        var payload = RandomBytes(5 * 1024 * 1024);
        var chunks = (payload.Length + chunk - 1) / chunk;

        Header("E. Live trace of a follower",
            $"{payload.Length / 1024 / 1024} MiB in {chunks} x {chunk / 1024} KiB chunks, {delayMs}ms apart");
        Console.WriteLine("   WRITE is the writer; READ and WAIT are the reader. Thread ids are pool threads and");
        Console.WriteLine("   hop between continuations -- the roles are concurrent, the numbers are not identities.");

        var path = TempPath();
        var entry = new CacheEntry { Path = path };
        var tracer = new Tracer();
        void Trace(string tag, string text) => tracer.Line(tag, text);

        var sink = new MemoryStream(payload.Length);
        var reads = 0;
        var waits = 0;

        var writer = WriteAsync(entry, payload, chunk, delayMs, written =>
        {
            Trace("WRITE", $"chunk {written / chunk,2}/{chunks}  flushed, file is now {written,9:N0} bytes");
            return Task.CompletedTask;
        });

        await WaitForFile(path);

        // Deliberately its own thread, not a continuation on the writer's: the console interleaving
        // below is only meaningful if the reader is genuinely running alongside the writer.
        var reader = Task.Run(async () =>
        {
            await FollowAsync(entry, sink,
                onRead: (count, position) =>
                {
                    Interlocked.Increment(ref reads);
                    Trace("READ", $"+{count,6:N0} bytes            consumed  {position,9:N0} bytes");
                },
                onWait: () =>
                {
                    Interlocked.Increment(ref waits);
                    Trace("WAIT", "caught up -- parked until the next write publishes");
                });
        });

        await Task.WhenAll(writer, reader);
        Trace("DONE", $"writer closed, reader drained and exited after {reads} reads / {waits} waits");

        Console.WriteLine();
        Check("the reader reconstructed the payload byte for byte",
            sink.ToArray().AsSpan().SequenceEqual(payload));
        Check("the reader consumed the body incrementally rather than in one gulp", reads > 1);
        Check("the reader parked and was woken at least once (it outran the writer)", waits > 0);

        Cleanup(path);
    }

    /// <summary>
    ///     Scenario A's staggered joins, narrated the way scenario E narrates a single reader. Five readers
    ///     enter the same growing file at five different points and each prints as its own bytes land.
    ///     The late joiners are the interesting ones: they burst through the backlog in a few large reads,
    ///     then drop into lockstep with the writer alongside everyone else.
    /// </summary>
    private static async Task ScenarioF_FiveTracedReaders()
    {
        const int chunk = 64 * 1024;
        const int delayMs = 100;
        var payload = RandomBytes(20 * chunk);
        var chunks = payload.Length / chunk;
        var expected = Convert.ToHexStringLower(SHA256.HashData(payload));

        // Spread across the write so one reader starts cold, three join mid-stream, and one arrives
        // only after Closed -- the four distinct states a subscriber can show up in.
        int[] joinAtMs = [0, 300, 800, 1500, 2600];

        Header("F. Five readers, live, joining at different points",
            $"{payload.Length / 1024} KiB in {chunks} x {chunk / 1024} KiB chunks, {delayMs}ms apart");
        Console.WriteLine("   R1..R5 are independent readers, each with its own FileStream on the same file.");

        var path = TempPath();
        var entry = new CacheEntry { Path = path };
        var tracer = new Tracer();

        var writer = WriteAsync(entry, payload, chunk, delayMs, written =>
        {
            tracer.Line("WRITE", $"chunk {written / chunk,2}/{chunks}  file is now {written,9:N0} bytes");
            return Task.CompletedTask;
        });

        await WaitForFile(path);

        async Task<(string Hash, int Reads, int Waits)> Reader(int index)
        {
            var label = $"R{index + 1}";
            await Task.Delay(joinAtMs[index]);

            var joinedAfterClose = entry.Closed;
            tracer.Line(label, $"opening its own FileStream ({(joinedAfterClose ? "writer already finished" : "writer still writing")})");

            var reads = 0;
            var waits = 0;
            await using var hasher = new HashingStream();
            await FollowAsync(entry, hasher,
                onRead: (count, position) =>
                {
                    reads++;
                    tracer.Line(label, $"+{count,6:N0} bytes   consumed {position,9:N0} / {payload.Length:N0}");
                },
                onWait: () =>
                {
                    waits++;
                    tracer.Line(label, "caught up -- parked until the next write");
                });

            tracer.Line(label, $"finished after {reads} reads / {waits} waits");
            return (hasher.Hex(), reads, waits);
        }

        var results = await Task.WhenAll(Enumerable.Range(0, joinAtMs.Length).Select(Reader));
        await writer;

        Console.WriteLine();
        for (var i = 0; i < results.Length; i++)
            Check($"R{i + 1} (joined at {joinAtMs[i],4}ms) reconstructed the payload exactly",
                results[i].Hash == expected);

        // Read *count* is capped by the 64 KiB buffer, so every reader needs the same 20 reads however
        // much was already on disk when it arrived. What separates them is waiting: a reader that tracks
        // the writer parks once per chunk, while one arriving after Closed never parks at all.
        Check($"the cold-start reader parked once per chunk ({results[0].Waits} waits)", results[0].Waits > 1);
        Check($"the reader that joined after close never parked ({results[^1].Waits} waits)",
            results[^1].Waits == 0);
        Check("later joiners parked strictly less than earlier ones " +
              $"({string.Join(" > ", results.Select(r => r.Waits))})",
            results.Zip(results.Skip(1)).All(pair => pair.First.Waits >= pair.Second.Waits));

        Cleanup(path);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Harness
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Hashes as it is written so a reader can verify a large body without buffering it.</summary>
    private sealed class HashingStream : Stream
    {
        private readonly IncrementalHash Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public string Hex() => Convert.ToHexStringLower(Hash.GetCurrentHash());

        public override void Write(byte[] buffer, int offset, int count) =>
            Hash.AppendData(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            Hash.AppendData(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) Hash.Dispose();
            base.Dispose(disposing);
        }
    }

    private static string TempPath() =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "follow-probe-" + Guid.NewGuid().ToString("n"));

    private static byte[] RandomBytes(int count)
    {
        var bytes = new byte[count];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }

    /// <summary>The writer creates the file asynchronously; readers must not open before it exists.</summary>
    private static async Task WaitForFile(string path)
    {
        for (var i = 0; i < 500 && !File.Exists(path); i++) await Task.Delay(2);
    }

    private static void Cleanup(string path)
    {
        try { File.Delete(path); } catch (IOException) { }
    }

    /// <summary>
    ///     Serialised console output for the traced scenarios. The timestamp is taken inside the lock, so
    ///     printed order and timestamp order always agree even with several threads logging at once.
    /// </summary>
    private sealed class Tracer
    {
        private readonly Stopwatch Clock = Stopwatch.StartNew();
        private readonly object Gate = new();

        public long ElapsedMs => Clock.ElapsedMilliseconds;

        public void Line(string tag, string text)
        {
            lock (Gate)
                Console.WriteLine($"    [{Clock.ElapsedMilliseconds,6}ms] [thread {Environment.CurrentManagedThreadId,3}] " +
                                  $"{tag,-5} {text}");
        }
    }

    private static void Header(string title, string detail)
    {
        Console.WriteLine();
        Console.WriteLine($"── {title}");
        Console.WriteLine($"   {detail}");
    }

    private static void Check(string what, bool ok)
    {
        if (!ok) Failures++;
        Console.WriteLine($"    [{(ok ? "PASS" : "FAIL")}] {what}");
    }
}
