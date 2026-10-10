using Dom.Store;
using Microsoft.AspNetCore.Mvc;

namespace Dom.Controllers;

/// <summary>
///     The two things every endpoint in Dom does: find the caller's token, and answer with the error
///     envelope the rest of the stack uses. One copy, so the two controllers cannot drift apart.
/// </summary>
internal static class Api
{
    /// <summary>The token from an <c>Authorization: Bearer …</c> header, if there is one.</summary>
    public static string? Bearer(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();

        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim()
            : null;
    }

    /// <summary>The account the request's bearer token belongs to, or <c>null</c> when it names none.</summary>
    public static User? Caller(this DomStore store, HttpRequest request) => store.Resolve(Bearer(request));

    public static JsonResult SignInFirst() => Error(401, DomStore.SignInFirst);

    public static JsonResult Invalid(string message) => Error(400, "invalid_request", message);

    public static JsonResult Error(int status, string code, string message) =>
        new(new { error = new { code, message } }) { StatusCode = status };

    /// <summary>
    ///     A refusal as the API answers it. A wrong password on a change is <c>403</c>, not the <c>401</c> sign-in
    ///     uses: the token is fine, and a client reads <c>401</c> as "you are signed out". A playlist the caller
    ///     cannot see is a bare <c>404</c>, the same answer as one that never existed.
    /// </summary>
    public static IActionResult Refuse(Refusal refusal) => refusal.Failure switch
    {
        Failure.NotFound => new NotFoundResult(),
        Failure.Unauthorized => Error(401, refusal),
        Failure.InvalidCredentials or Failure.Forbidden => Error(403, refusal),
        Failure.UsernameTaken or Failure.Stale => Error(409, refusal),
        Failure.InvalidCode => Error(404, refusal),
        _ => Error(400, refusal)
    };

    public static JsonResult Error(int status, Refusal refusal) => Error(status, refusal.Code, refusal.Message);
}
