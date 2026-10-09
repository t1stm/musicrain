using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace Moliv;

/// <summary>
///     Who a request is from. Every one carries <c>X-Device-Id</c>; a signed-in one also carries the bearer
///     token Dom handed out, which Dom's <c>/Me</c> turns into the account's stable ID.
/// </summary>
/// <remarks>
///     ponytail: an answer is kept five minutes, so a revoked token can still write history for up to
///     five minutes. Have Dom push revocations if that ever matters.
/// </remarks>
public sealed class Identity(IHttpClientFactory http, IConfiguration configuration, IMemoryCache cache)
{
    private static readonly TimeSpan Remembered = TimeSpan.FromMinutes(5);

    private readonly string _dom = (configuration["Dom:Url"] ?? "http://dom:8080").TrimEnd('/');

    /// <summary>The last time Dom could not be asked, and why. For Oko.</summary>
    public string? LastError { get; private set; }

    /// <returns>The caller, or the answer to refuse them with.</returns>
    public async Task<(Caller? caller, IResult? refusal)> WhoAsync(HttpRequest request, CancellationToken ct)
    {
        if (!Guid.TryParseExact(request.Headers["X-Device-Id"].ToString(), "D", out var parsed))
            return (null, Api.Error(400, "invalid_request", "Send X-Device-Id, a UUID."));
        var device = parsed.ToString();

        var authorization = request.Headers.Authorization.ToString();
        if (authorization.Length == 0) return (new Caller(device, null), null);

        // A token Dom refuses is a 401, never a quiet fall back to anonymous: that would file a signed-in
        // listener's plays under the device, where the next person to sign in on it would claim them.
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return (null, SignInAgain());
        var token = authorization["Bearer ".Length..].Trim();

        if (cache.TryGetValue(token, out string? known)) return (new Caller(device, known), null);

        try
        {
            using var ask = new HttpRequestMessage(HttpMethod.Get, $"{_dom}/Audio/Accounts/Me");
            ask.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var answer = await http.CreateClient("dom").SendAsync(ask, ct);
            if (answer.StatusCode == HttpStatusCode.Unauthorized) return (null, SignInAgain());
            answer.EnsureSuccessStatusCode();

            var me = await answer.Content.ReadFromJsonAsync<Me>(ct);
            if (me?.Id is not { Length: > 0 } id)
                throw new InvalidDataException("Dom answered /Me without an account id — is it older than Moliv?");

            cache.Set(token, id, Remembered);
            return (new Caller(device, id), null);
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or JsonException
                                              || (exception is TaskCanceledException && !ct.IsCancellationRequested))
        {
            // The client's outbox keeps the play and sends it again.
            LastError = $"{DateTimeOffset.UtcNow:O} {exception.Message}";
            return (null, Api.Error(503, "unavailable", "Accounts cannot be checked right now. Try again shortly."));
        }
    }

    private static IResult SignInAgain() => Api.Error(401, "unauthorized", "Sign in again.");

    private sealed record Me(string? Id);
}
