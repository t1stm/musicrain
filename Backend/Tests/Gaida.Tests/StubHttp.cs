using System.Net;
using System.Text;

namespace Gaida.Tests;

/// <summary>One canned transport: every request goes to <paramref name="answer" />, and is counted.</summary>
internal sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> answer) : HttpMessageHandler
{
    private int _requests;

    public int Requests => Volatile.Read(ref _requests);

    public static HttpResponseMessage Reply(HttpStatusCode status, string body = "",
        string contentType = "application/json")
    {
        return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, contentType) };
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _requests);
        return answer(request);
    }
}

internal sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name)
    {
        return new HttpClient(handler, false);
    }
}

/// <summary>A real loopback HTTP server, for code that builds its own <see cref="HttpClient" />.</summary>
internal sealed class LoopbackServer : IDisposable
{
    private readonly HttpListener _listener = new();

    public LoopbackServer(Func<HttpListenerContext, Task> answer)
    {
        var port = FreePort();
        BaseUrl = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(BaseUrl + "/");
        _listener.Start();
        _ = Serve(answer);
    }

    public string BaseUrl { get; }

    public void Dispose()
    {
        _listener.Close();
    }

    public static async Task Write(HttpListenerContext context, HttpStatusCode status, string body = "",
        string contentType = "application/json")
    {
        context.Response.StatusCode = (int)status;
        context.Response.ContentType = contentType;
        var bytes = Encoding.UTF8.GetBytes(body);
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private async Task Serve(Func<HttpListenerContext, Task> answer)
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException)
            {
                return;
            }

            _ = answer(context);
        }
    }

    private static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
