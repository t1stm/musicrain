using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Gaida.Admin;
using JetBrains.Annotations;
using Microsoft.Extensions.Options;

namespace Oko;

/// <summary>Who is asking, as the handlers and the audit log see them.</summary>
/// <param name="Permissions">The effective set from Dom's <c>/Me</c>: every permission for the Owner.</param>
public sealed record Caller(string Name, string? Role, string[] Permissions)
{
    /// <summary>Whether this operator may act on a target that needs <paramref name="permission" />.</summary>
    public bool Can(string? permission) => Staff.Can(Role, Permissions, permission);
}

/// <summary>A request the door turned away: the status, and the stack's error envelope to send with it.</summary>
public sealed record Refusal(int Status, string Code, string Message)
{
    public IResult Result() => Results.Json(new { error = new { code = Code, message = Message } }, statusCode: Status);
}

/// <summary>
///     Who is asking, from Dom. Every /Audio/Admin request carries the caller's own bearer token, and Dom's
///     /Me says whether it is staff. Not cached: admin traffic is a few requests a second per open tab, Dom
///     answers from memory, and an uncached answer means a demoted admin is refused on their next request.
///     ponytail: an open /events stream is checked once, when it opens; it ends at its next reconnect.
/// </summary>
public sealed class Door(IHttpClientFactory http, IOptions<OkoOptions> options) : IEndpointFilter
{
    private readonly string _dom = (options.Value.DomUrl ?? "").TrimEnd('/');

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var request = context.HttpContext.Request;
        var (who, refusal) = await WhoAsync(request.Headers.Authorization.ToString(), context.HttpContext.RequestAborted);
        if (refusal is not null) return refusal.Result();

        context.HttpContext.Items[typeof(Caller)] = who;
        return await next(context);
    }

    /// <returns>The staff member behind <paramref name="authorization" />, or why they are refused.</returns>
    public async Task<(Caller? Who, Refusal? Refusal)> WhoAsync(string authorization, CancellationToken ct)
    {
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return (null, new Refusal(401, "unauthorized", "Sign in first."));

        StaffMe? me;
        try
        {
            using var ask = new HttpRequestMessage(HttpMethod.Get, $"{_dom}/Audio/Accounts/Me");
            ask.Headers.Authorization = AuthenticationHeaderValue.Parse(authorization);

            using var answer = await http.CreateClient("dom").SendAsync(ask, ct);
            if (answer.StatusCode == HttpStatusCode.Unauthorized)
                return (null, new Refusal(401, "unauthorized", "Sign in again."));
            answer.EnsureSuccessStatusCode();

            me = await answer.Content.ReadFromJsonAsync<StaffMe>(JsonSerializerOptions.Web, ct);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or FormatException
                                              || (exception is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return (null, Unavailable);
        }

        if (me is null or { Username: null }) return (null, Unavailable);
        if (!me.IsStaff) return (null, new Refusal(403, "not_staff", "This is for staff."));

        return (new Caller(me.Username, me.Role, me.Permissions ?? []), null);
    }

    private static readonly Refusal Unavailable =
        new(503, "unavailable", "Accounts cannot be checked right now. Try again shortly.");
}

/// <summary>One target as the panel lists it: its name, and the permission its actions need.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record TargetInfo(string Name, string? Permission);
