using Dom.Store;
using Microsoft.AspNetCore.Mvc;

namespace Dom.Controllers;

/// <summary>
///     Register, sign in, say who you are, sign out. Everything else Dom will own hangs off the
///     account this hands out a token for.
/// </summary>
/// <remarks>
///     ponytail: nothing rate-limits <see cref="Register" /> or <see cref="Login" />. On a public host
///     that is a spam and password-guessing surface — see the open questions in PLAYLISTS_PLAN.md. The
///     cheapest answer if it becomes real is an invite code read from configuration.
/// </remarks>
public class Accounts(ILogger<Accounts> logger, DomStore store) : ControllerBase
{
    [HttpPost("/Audio/Accounts/Register")]
    public IActionResult Register([FromBody] Credentials? body)
    {
        if (body is null) return Api.Error(400, "invalid_request", "Send a username and a password.");

        var (token, user, error, message) = store.Register(body.Username ?? "", body.Password ?? "");
        if (error is not null)
            return Api.Error(error == "username_taken" ? 409 : 400, error, message!);

        logger.LogInformation("New account: {Username}", user!.Username);

        return new JsonResult(Session(user, token!)) { StatusCode = 201 };
    }

    [HttpPost("/Audio/Accounts/Login")]
    public IActionResult Login([FromBody] Credentials? body)
    {
        if (body is null) return Api.Error(400, "invalid_request", "Send a username and a password.");

        var (token, user, error, message) = store.Login(body.Username ?? "", body.Password ?? "");
        return error is not null ? Api.Error(401, error, message!) : new JsonResult(Session(user!, token!));
    }

    /// <summary>
    ///     Who the caller is, and how long their token is good for. The expiry slides on use, so the
    ///     copy the client kept from its sign-in only ever understates it — this is where the client
    ///     reads the current one.
    /// </summary>
    [HttpGet("/Audio/Accounts/Me")]
    public IActionResult Me()
    {
        var token = Api.Bearer(Request);
        var user = store.Resolve(token);
        return user is null
            ? Api.Error(401, "unauthorized", "Sign in first.")
            : new JsonResult(new
            {
                username = user.Username,
                createdUtc = user.CreatedUtc,
                expiresUtc = store.ExpiryOf(token)
            });
    }

    [HttpPost("/Audio/Accounts/Logout")]
    public IActionResult Logout()
    {
        store.Logout(Api.Bearer(Request));

        // A token that was already gone is a signed-out caller either way; saying so with a 401 only
        // makes the client handle an outcome it asked for.
        return NoContent();
    }

    private static object Session(User user, Token token) => new
    {
        username = user.Username,
        token = token.Value,
        expiresUtc = token.ExpiresUtc
    };

    /// <summary>
    ///     What the browser sends. It may hash the password before it gets here; that changes nothing
    ///     server-side, because whatever arrives is the secret and is hashed again on arrival.
    /// </summary>
    public sealed record Credentials(string? Username, string? Password);
}