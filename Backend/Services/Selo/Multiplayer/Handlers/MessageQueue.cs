using System.Text;

namespace Selo.Multiplayer.Handlers;

public class MessageQueue(UserStore store)
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
                catch (Exception)
                {
                    // a user went kaboom, will be removed on next heartbeat by other handlers
                }
        }
        finally
        {
            _sync.Release();
            message.Dispose();
        }
    }
}
