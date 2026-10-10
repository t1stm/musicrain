using System.Diagnostics;
using Gaida.Core.Platforms;
using Gaida.Core.Streams;
using Gaida.Platforms.MusicDatabase;
using Gaida.Infrastructure.FFmpeg;
using Gaida.Platforms.MusicDatabase.Getters;
using Gaida.Platforms.YouTube.Getters;
using Serilog.Core;

namespace Pods.Tests;

/// <summary>
///     The three places a request hands its bytes to an external process: ffmpeg, wvunpack and yt-dlp. Real
///     binaries, no network: every input is generated here, and yt-dlp is only ever pointed at a URL it refuses.
/// </summary>
public class MediaToolTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("gaida-media").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch (IOException) { /* temp dir */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task FFmpegEncodesTheWholeSource()
    {
        await using var source = File.OpenRead(await Wav(1));
        using var output = new MemoryStream();

        await FFmpegEncoder.EncodeAsync(source, output, "-c:a flac -f flac");

        Assert.Equal("fLaC"u8.ToArray(), output.ToArray()[..4]);
    }

    [Fact]
    public async Task ACancelledEncodeLeavesNoFFmpegBehind()
    {
        // Thirty seconds of audio is far more than a pipe buffer of FLAC, so ffmpeg has output it cannot hand
        // over once nobody reads — the case where a skipped track used to leave the process blocked for good.
        await using var source = File.OpenRead(await Wav(30));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FFmpegEncoder.EncodeAsync(source, new StalledStream(), "-c:a flac -f flac", cancel.Token));

        Assert.True(await NoChildNamed("ffmpeg"), "ffmpeg was still running after the encode was cancelled");
    }

    [Fact]
    public async Task APlainLibraryFileIsServedAsItIs()
    {
        var file = await Wav(1);
        var spreader = await new MusicGetter(Logger.None).GetContentDataAsync(Local(file), CancellationToken.None);

        Assert.NotNull(spreader);
        Assert.True(spreader.Closed);
        Assert.Equal(await File.ReadAllBytesAsync(file), await ReadToEnd(spreader));
    }

    [Fact]
    public async Task AHybridWavPackTrackIsDecodedToFlac()
    {
        var spreader = await new MusicGetter(Logger.None).GetContentDataAsync(Local(await Hybrid()),
            CancellationToken.None);

        Assert.NotNull(spreader);
        Assert.Equal("fLaC"u8.ToArray(), (await ReadToEnd(spreader))[..4]);
    }

    [Fact]
    public async Task AHybridDecodeThatIsCancelledStillEndsTheBody()
    {
        var hybrid = await Hybrid();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var spreader = await new MusicGetter(Logger.None).GetContentDataAsync(Local(hybrid), cancelled.Token);

        // A body nobody closes keeps every reader waiting for bytes that are never coming.
        Assert.NotNull(spreader);
        await ReadToEnd(spreader);
    }

    [Fact]
    public async Task AYtDlpDownloadThatFailsStillEndsTheBody()
    {
        var spreader = await new GetterYtDlp(Logger.None).GetContentDataAsync(new Refused(),
            CancellationToken.None);

        Assert.NotNull(spreader);
        Assert.Empty(await ReadToEnd(spreader));
    }

    [Fact]
    public async Task AYtDlpDownloadThatIsCancelledStillEndsTheBody()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var spreader = await new GetterYtDlp(Logger.None).GetContentDataAsync(new Refused(), cancelled.Token);

        Assert.NotNull(spreader);
        await ReadToEnd(spreader);
    }

    private async Task<string> Wav(int seconds)
    {
        var path = Path.Combine(_directory, $"tone-{seconds}.wav");
        if (!File.Exists(path))
            await Run("ffmpeg", "-v", "quiet", "-y", "-f", "lavfi", "-i", $"sine=frequency=440:duration={seconds}",
                "-ac", "2", "-ar", "44100", path);
        return path;
    }

    /// <summary>A .wv with a .wvc beside it: the one shape <c>MusicGetter</c> decodes instead of adopting.</summary>
    private async Task<string> Hybrid()
    {
        var path = Path.Combine(_directory, "hybrid.wv");
        await Run("wavpack", "-q", "-y", "-b384", "-c", await Wav(1), "-o", path);
        Assert.True(File.Exists(path + "c"), "wavpack wrote no correction file");
        return path;
    }

    private static MusicResult Local(string path)
    {
        return new MusicResult { Id = "audio://test", Downloaders = [], Path = path };
    }

    private static async Task<byte[]> ReadToEnd(StreamSpreader spreader)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var reader = spreader.OpenRead();
        using var sink = new MemoryStream();
        await reader.CopyToAsync(sink, timeout.Token);
        return sink.ToArray();
    }

    private static async Task Run(string file, params string[] arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = false })!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
    }

    /// <summary>Waits a few seconds for every child process of this one with that name to be gone.</summary>
    private static async Task<bool> NoChildNamed(string name)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (!Children().Contains(name)) return true;
            await Task.Delay(100);
        }

        return false;
    }

    private static IEnumerable<string> Children()
    {
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            string stat;
            try
            {
                stat = File.ReadAllText(Path.Combine(directory, "stat"));
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            // pid (comm) state ppid ... — comm may hold spaces, so split after its closing bracket.
            var open = stat.IndexOf('(');
            var close = stat.LastIndexOf(')');
            if (open < 0 || close < open) continue;

            var fields = stat[(close + 2)..].Split(' ');
            if (fields.Length > 1 && fields[0] != "Z" && fields[1] == Environment.ProcessId.ToString())
                yield return stat[(open + 1)..close];
        }
    }

    /// <summary>A destination that never takes a byte, the way a client that stopped reading looks to a copy.</summary>
    private sealed class StalledStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>A result yt-dlp turns away at once, offline: file URLs are off unless it is told otherwise.</summary>
    private sealed class Refused : PlatformResult
    {
        [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
        public Refused()
        {
            Id = "test://refused";
            Downloaders = [];
        }

        public override string GetDownloadUrl()
        {
            return "file:///nonexistent";
        }
    }
}
