using System.Collections.Concurrent;
using Gaida.Platforms.MusicDatabase;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Pods.Tests;

/// <summary>The scan the pod starts at boot and does not wait for: a failure has to say so somewhere.</summary>
public class LibraryBootTests : IDisposable
{
    private readonly string _notADirectory = Path.GetTempFileName();

    public void Dispose()
    {
        File.Delete(_notADirectory);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ABootScanThatFailsIsLogged()
    {
        // A file where the library should be: the scan cannot even create its directory.
        var sink = new CollectingSink();
        var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();

        new MusicDatabase(logger, new LibraryOptions(_notADirectory)).Initialize();

        for (var waited = 0; waited < 50 && !sink.Events.Any(e => e.Level >= LogEventLevel.Error); waited++)
            await Task.Delay(100);

        Assert.Contains(sink.Events, e => e.Level >= LogEventLevel.Error && e.Exception is IOException);
    }

    private sealed class CollectingSink : ILogEventSink
    {
        public ConcurrentQueue<LogEvent> Events { get; } = new();

        public void Emit(LogEvent logEvent)
        {
            Events.Enqueue(logEvent);
        }
    }
}
