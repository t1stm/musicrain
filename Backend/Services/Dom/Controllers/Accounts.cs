using System.Text.Json.Nodes;
using Dom.Store;
using Microsoft.AspNetCore.Mvc;

namespace Dom.Controllers;

/// <summary>
///     Register, sign in, say who you are, sign out — and what the account holder can change about
///     their own account: its settings, its name, its password, whether it exists. Everything else
///     Dom owns hangs off the account this hands out a token for.
/// </summary>
/// <remarks>
///     ponytail: nothing rate-limits <see cref="Register" /> or <see cref="Login" />. On a public host
///     that is a spam and password-guessing surface — see the open questions in PLAYLISTS_PLAN.md. The
///     cheapest answer if it becomes real is an invite code read from configuration.
/// </remarks>
public class Accounts(ILogger<Accounts> logger, DomStore store, IConfiguration config) : ControllerBase
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

    /// <summary>The account's settings, or <c>"settings": null</c> if it never saved any — which is the client's cue to seed them.</summary>
    [HttpGet("/Audio/Accounts/Settings")]
    public IActionResult GetSettings()
    {
        var user = store.Resolve(Api.Bearer(Request));
        if (user is null) return Api.Error(401, "unauthorized", "Sign in first.");

        var (settings, updatedUtc) = store.Settings(user);
        return new JsonResult(new { settings, updatedUtc });
    }

    /// <summary>Merges the keys sent into the saved settings. A key sent as <c>null</c> is removed.</summary>
    [HttpPatch("/Audio/Accounts/Settings")]
    public IActionResult PatchSettings([FromBody] JsonNode? body)
    {
        var user = store.Resolve(Api.Bearer(Request));
        if (user is null) return Api.Error(401, "unauthorized", "Sign in first.");
        if (body is not JsonObject patch)
            return Api.Error(400, "invalid_request", "Send the settings as a JSON object.");

        var (settings, updatedUtc) = store.MergeSettings(user, patch);
        return new JsonResult(new { settings, updatedUtc });
    }

    /// <summary>Signs out every other device. Needs no password: it only takes access away.</summary>
    [HttpPost("/Audio/Accounts/SignOutEverywhere")]
    public IActionResult SignOutEverywhere()
    {
        var token = Api.Bearer(Request);
        var user = store.Resolve(token);
        if (user is null) return Api.Error(401, "unauthorized", "Sign in first.");

        return new JsonResult(new { revoked = store.SignOutEverywhere(user, token!) });
    }

    /// <summary>
    ///     Changes the password. Every token is revoked, this one included, so the answer is a fresh
    ///     session for the device that asked.
    /// </summary>
    [HttpPost("/Audio/Accounts/Password")]
    public IActionResult Password([FromBody] PasswordChange? body)
    {
        var user = store.Resolve(Api.Bearer(Request));
        if (user is null) return Api.Error(401, "unauthorized", "Sign in first.");
        if (body is null) return Api.Error(400, "invalid_request", "Send the current password and a new one.");

        var (token, error, message) = store.ChangePassword(user, body.Current, body.Password);
        if (error is not null) return Refusal(error, message!);

        logger.LogInformation("{Username} changed their password", user.Username);
        return new JsonResult(Session(user, token!));
    }

    [HttpPost("/Audio/Accounts/Rename")]
    public IActionResult Rename([FromBody] Credentials? body)
    {
        var user = store.Resolve(Api.Bearer(Request));
        if (user is null) return Api.Error(401, "unauthorized", "Sign in first.");
        if (body is null) return Api.Error(400, "invalid_request", "Send the new username and your password.");

        var previous = user.Username;
        var (error, message) = store.Rename(user, body.Password, body.Username);
        if (error is not null) return Refusal(error, message!);

        logger.LogInformation("{Previous} is now {Username}", previous, user.Username);
        return new JsonResult(new { username = user.Username });
    }

    /// <summary>
    ///     Deletes the account, its playlists and their covers. <c>POST</c> rather than <c>DELETE</c>
    ///     because it carries a body, and some proxies drop the body of a <c>DELETE</c>.
    /// </summary>
    [HttpPost("/Audio/Accounts/Delete")]
    public IActionResult Delete([FromBody] Confirmation? body)
    {
        var user = store.Resolve(Api.Bearer(Request));
        if (user is null) return Api.Error(401, "unauthorized", "Sign in first.");

        var (error, message, covers) = store.DeleteAccount(user, body?.Password);
        if (error is not null) return Refusal(error, message!);

        var directory = config["Dom:CoverDir"] ?? "covers";
        foreach (var cover in covers) Admin.Forget(directory, cover);

        return NoContent();
    }

    /// <summary>
    ///     A wrong password on a change is <c>403</c>, not the <c>401</c> <see cref="Login" /> uses: the
    ///     token is fine, and a client reads <c>401</c> as "you are signed out".
    /// </summary>
    private static JsonResult Refusal(string error, string message) => Api.Error(error switch
    {
        "unauthorized" => 401,
        "invalid_credentials" => 403,
        "username_taken" => 409,
        _ => 400
    }, error, message);

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

    public sealed record PasswordChange(string? Current, string? Password);

    public sealed record Confirmation(string? Password);
}