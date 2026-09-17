namespace Gaida.Core.Streams;

/// <summary>
///     Managed file stream that allows a single writer to send data across any number of independent readers.
/// </summary>
/// <remarks>
///     This works because a file can be read while it is being written. On Linux there is no mandatory
///     locking at all; on Windows it is legal as long as every opener agrees on the sharing mode, which
///     is why <see cref="Share" /> is used on both sides and is not decoration.
/// </remarks>
public class StreamSpreader : Stream
{
    /// <summary>
    ///     <c>ReadWrite</c> lets the writer and the readers hold the file at the same time. <c>Delete</c> lets
    ///     it be evicted while they do -- which does not cut anyone off: unlinking removes the directory
    ///     entry, and the data lives until the last descriptor closes, so a reader mid-body finishes normally
    ///     and the space returns when it lets go. On Windows this flag is what makes that legal at all;
    ///     without it <see cref="File.Delete" /> throws while any handle is open. Only opens that happen
    ///     <em>after</em> the unlinking fails, which callers handle as a cache miss.
    /// </summary>
    private const FileShare Share = FileShare.ReadWrite | FileShare.Delete;

    private const int BufferSize = 64 * 1024;

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private FileStream? _file;
    private string? _keepAsPath;
    private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <param name="path">
    /// Where the body lives. Defaults to a fresh temp file. Pass a path to write straight to its final
    /// home -- the caller then almost certainly wants <paramref name="deleteOnClose" /> <c>false</c>.
    /// </param>
    /// <param name="deleteOnClose">
    /// Whether <see cref="Dispose" /> removes the file. True for scratch bodies, false when the file 
    /// already existed or is meant to outlive the spreader. Should be set to true for paths from Path.GetTempFileName()
    /// otherwise there is a risk of overflowing the 65535 limit.
    /// </param>
    public StreamSpreader(string? path = null, bool deleteOnClose = true)
    {
        Path = path ?? System.IO.Path.GetTempFileName();
        DeleteOnClose = deleteOnClose;
    }

    /// <summary>The file backing this body. Stable except across <see cref="KeepAs" />, which moves it on close.</summary>
    public string Path { get; private set; }

    private bool DeleteOnClose { get; set; }

    /// <summary>Set once the source data is complete. A reader that observes this and then reads 0 bytes is done.</summary>
    public bool Closed { get; private set; }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => !Closed;
    public override long Length => Position;

    /// <summary>Bytes written so far. Readers never consult this -- see <see cref="Reader.ReadAsync(byte[], int, int)" />.</summary>
    public override long Position { get; set; }

    /// <summary>Completes on the next write or on close. Capture it <em>before</em> testing <see cref="Closed" />.</summary>
    private Task Changed => Volatile.Read(ref _signal).Task;

    /// <summary>
    ///     Adopts a file that already exists without copying a byte of it. The spreader is complete from the
    ///     outset and never touches the file except to read it, so a local library track or an already-cached
    ///     download costs nothing to serve.
    /// </summary>
    public static StreamSpreader FromExistingFile(string path)
    {
        return new StreamSpreader(path, false)
        {
            Closed = true,
            Position = new FileInfo(path).Length
        };
    }

    /// <summary>
    ///     Moves the body to <paramref name="path" /> when it closes, instead of leaving it in scratch space.
    ///     Lets a consumer that only learns after the fact that a download is worth keeping say so, without
    ///     writing a second copy alongside the first.
    /// </summary>
    public void KeepAs(string path)
    {
        _keepAsPath = path;
    }

    /// <summary>A private read handle. Callers own it and must dispose of it.</summary>
    /// <remarks>
    ///     Seekable once <see cref="Closed" />, so a finished body can answer HTTP range requests directly
    ///     rather than being materialized in memory first.
    /// </remarks>
    public Stream OpenRead()
    {
        return new Reader(this, new FileStream(Path, FileMode.Open, FileAccess.Read, Share, BufferSize,
            FileOptions.Asynchronous));
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            // Opened lazily so that a spreader nobody writes to -- FromExistingFile, or a getter that bails
            // before its first byte -- never truncates anything.
            // bufferSize 0: no user-space buffering, so the flush below is the only thing between a write
            // and a reader being able to see those bytes.
            _file ??= new FileStream(Path, FileMode.Create, FileAccess.Write, Share, 0, FileOptions.Asynchronous);

            await _file.WriteAsync(buffer, cancellationToken);

            // To the OS, not fsync. Readers share the page cache, so this is all it takes for them to see
            // the bytes, and a body lost to a power cut is a cache miss rather than data loss.
            await _file.FlushAsync(cancellationToken);

            Publish(Position + buffer.Length, false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    }

    /// <summary>Marks the end of the source data. Not to be confused with <see cref="Stream.Close" />, which disposes.</summary>
    public async Task CloseAsync()
    {
        await _writeLock.WaitAsync();
        try
        {
            if (Closed) return;

            if (_file is not null) await _file.DisposeAsync();
            _file = null;

            if (_keepAsPath is not null) Relocate(_keepAsPath);

            Publish(Position, true);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    ///     Moves the finished body to its keep-as home and adopts it. Readers already streaming hold handles
    ///     on the old inode and are unaffected -- on Linux that is true whether the move is a rename or, across
    ///     devices, a copy and unlink. Readers opened afterwards find the file at its new path.
    /// </summary>
    private void Relocate(string destination)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
            File.Move(Path, destination, true);

            Path = destination;
            DeleteOnClose = false;
        }
        catch (IOException)
        {
            // Keeping the body is opportunistic -- a cache that could not be written is not a failed request.
        }
    }

    private void Publish(long position, bool closed)
    {
        Position = position;
        Closed = closed;
        Interlocked.Exchange(ref _signal,
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).SetResult();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _file?.Dispose();
            _file = null;

            if (DeleteOnClose)
                try
                {
                    // Unlink, do not wait. Readers still streaming hold their own handles and the inode
                    // outlives the directory entry, so the space comes back when the last of them closes.
                    File.Delete(Path);
                }
                catch (IOException)
                {
                    // A temp file that could not be removed is a leak, not a failure.
                }
        }

        base.Dispose(disposing);
    }

    /// <summary>One consumer's view of the body: an ordinary readable stream that waits rather than ending early.</summary>
    private sealed class Reader(StreamSpreader owner, FileStream fileStream) : Stream
    {
        public override bool CanRead => true;

        /// <summary>Only once the body is complete -- seeking a still-growing file gives a length that is already stale.</summary>
        public override bool CanSeek => owner.Closed;

        public override bool CanWrite => false;
        public override long Length => fileStream.Length;

        public override long Position
        {
            get => fileStream.Position;
            set => fileStream.Position = value;
        }

        /// <summary>
        ///     Reads the next available bytes, waiting for the writer if the body is still growing and there is
        ///     nothing left to consume.
        /// </summary>
        /// <remarks>
        ///     Cannot end early: it only returns 0 when a real read returned 0 <em>and</em> the body was already
        ///     observed closed before that read, so anything the writer flushed is necessarily handed over
        ///     first. The one ordering rule is capturing <see cref="Changed" /> before testing
        ///     <see cref="Closed" /> -- the other way round, the writer can publish in the gap and leave the
        ///     reader parked on a signal that has already fired.
        /// </remarks>
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var changed = owner.Changed;
                var closed = owner.Closed;

                var read = await fileStream.ReadAsync(buffer, cancellationToken);
                if (read > 0) return read;
                if (closed) return 0;

                await changed.WaitAsync(cancellationToken);
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            return fileStream.Seek(offset, origin);
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) fileStream.Dispose();
            base.Dispose(disposing);
        }
    }

    #region Not Supported

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

    #endregion
}
