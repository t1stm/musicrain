using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Selo.Multiplayer.Handlers;

[SuppressMessage("Microsoft.Design", "CA1001:TypesThatOwnDisposableFieldsShouldBeDisposable",
    Justification = "The send lock is a SemaphoreSlim that never hands out its wait handle, so a room that is " +
                    "dropped leaves nothing behind for the collector to miss.")]
[SuppressMessage("Microsoft.Naming", "CA1711:IdentifiersShouldNotHaveIncorrectSuffix",
    Justification = "It is a queue of messages to send, not a collection type; the name is the one the room " +
                    "code uses.")]
public class MessageQueue(UserStore store, ILogger logger)
{
    private readonly SemaphoreSlim _sync = new(1);

    public UserStore CurrentStore => store;

    /// <summary>Broadcasts an interpolated frame without ever materialising it as a string.</summary>
    public Task Send(Utf8MessageHandler handler)
    {
        return Add(handler.Message);
    }

    public Task Add(string message)
    {
        var pooled = new Utf8Message(Encoding.UTF8.GetMaxByteCount(message.Length));
        pooled.Write(message.AsSpan());

        return Add(pooled);
    }

    /// <summary>Sends one pooled frame to every member, then returns its buffer.</summary>
    public async Task Add(Utf8Message message)
    {
        await _sync.WaitAsync();
        try
        {
            foreach (var (_, user) in store.Users)
                try
                {
                    await user.SendMessageAsync(message.Memory);
                }
                catch (Exception exception)
                {
                    // a user went kaboom, will be removed on next heartbeat by other handlers
                    logger.SendFailed(exception, user.Id);
                }
        }
        finally
        {
            _sync.Release();
            message.Dispose();
        }
    }
}
