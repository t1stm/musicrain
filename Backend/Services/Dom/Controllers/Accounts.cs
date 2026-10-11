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
public class Accounts(ILogger<Accounts> logger, DomStore store, AccountCleanup cleanup) : ControllerBase
{
    [HttpPost("/Audio/Accounts/Register")]
    public IActionResult Register([FromBody] Credentials? body)
    {
        if (body is null) return Api.Invalid("Send a username and a password.");

        var outcome = store.Register(body.Username ?? "", body.Password ?? "");
        if (!outcome.Ok) return Api.Refuse(outcome.Refusal);

        logger.NewAccount(outcome.Value.User.Username);

        return new JsonResult(Session(outcome.Value.User, outcome.Value.Token)) { StatusCode = 201 };
    }

    [HttpPost("/Audio/Accounts/Login")]
    public IActionResult Login([FromBody] Credentials? body)
    {
        if (body is null) return Api.Invalid("Send a username and a password.");

        // Every refusal here is a 401, wrong password included: see Api.Refuse for why a change differs.
        var outcome = store.Login(body.Username ?? "", body.Password ?? "");
        return outcome.Ok
            ? new JsonResult(Session(outcome.Value.User, outcome.Value.Token))
            : Api.Error(401, outcome.Refusal);
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
            ? Api.SignInFirst()
            : new JsonResult(new
            {
                id = user.Id,
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
        if (store.Caller(Request) is not { } user) return Api.SignInFirst();

        var (settings, updatedUtc) = store.Settings(user);
        return new JsonResult(new { settings, updatedUtc });
    }

    /// <summary>Merges the keys sent into the saved settings. A key sent as <c>null</c> is removed.</summary>
    [HttpPatch("/Audio/Accounts/Settings")]
    public IActionResult PatchSettings([FromBody] JsonNode? body)
    {
        if (store.Caller(Request) is not { } user) return Api.SignInFirst();
        if (body is not JsonObject patch)
            return Api.Invalid("Send the settings as a JSON object.");

        var (settings, updatedUtc) = store.MergeSettings(user, patch);
        return new JsonResult(new { settings, updatedUtc });
    }

    /// <summary>Signs out every other device. Needs no password: it only takes access away.</summary>
    [HttpPost("/Audio/Accounts/SignOutEverywhere")]
    public IActionResult SignOutEverywhere()
    {
        var token = Api.Bearer(Request);
        var user = store.Resolve(token);
        if (user is null) return Api.SignInFirst();

        return new JsonResult(new { revoked = store.SignOutEverywhere(user, token!) });
    }

    /// <summary>
    ///     Changes the password. Every token is revoked, this one included, so the answer is a fresh
    ///     session for the device that asked.
    /// </summary>
    [HttpPost("/Audio/Accounts/Password")]
    public IActionResult Password([FromBody] PasswordChange? body)
    {
        if (store.Caller(Request) is not { } user) return Api.SignInFirst();
        if (body is null) return Api.Invalid("Send the current password and a new one.");

        var outcome = store.ChangePassword(user, body.Current, body.Password);
        if (!outcome.Ok) return Api.Refuse(outcome.Refusal);

        logger.PasswordChanged(user.Username);
        return new JsonResult(Session(user, outcome.Value));
    }

    [HttpPost("/Audio/Accounts/Rename")]
    public IActionResult Rename([FromBody] Credentials? body)
    {
        if (store.Caller(Request) is not { } user) return Api.SignInFirst();
        if (body is null) return Api.Invalid("Send the new username and your password.");

        var previous = user.Username;
        if (store.Rename(user, body.Password, body.Username) is { } refusal) return Api.Refuse(refusal);

        logger.Renamed(previous, user.Username);
        return new JsonResult(new { username = user.Username });
    }

    /// <summary>
    ///     Deletes the account, its playlists and their covers. <c>POST</c> rather than <c>DELETE</c>
    ///     because it carries a body, and some proxies drop the body of a <c>DELETE</c>.
    /// </summary>
    [HttpPost("/Audio/Accounts/Delete")]
    public async Task<IActionResult> Delete([FromBody] Confirmation? body)
    {
        if (store.Caller(Request) is not { } user) return Api.SignInFirst();

        var outcome = store.DeleteAccount(user, body?.Password);
        if (!outcome.Ok) return Api.Refuse(outcome.Refusal);

        await cleanup.AfterDeleteAsync(user.Id, outcome.Value);

        return NoContent();
    }

    // ── Friends ────────────────────────────────────────────────────────────────────────────────
    // The code lives under /Invite rather than /Friends/Invite: a friend may be called "Invite",
    // and DELETE /Friends/{username} must be able to reach them.

    [HttpGet("/Audio/Accounts/Friends")]
    public IActionResult Friends()
    {
        return store.Caller(Request) is { } user ? new JsonResult(store.Friends(user)) : Api.SignInFirst();
    }

    /// <summary>Ends a friendship on both sides.</summary>
    [HttpDelete("/Audio/Accounts/Friends/{username}")]
    public IActionResult Unfriend(string username)
    {
        if (store.Caller(Request) is not { } user) return Api.SignInFirst();

        return store.Unfriend(user, username) ? NoContent() : NotFound();
    }

    /// <summary>The caller's live code, or a new one. Pressing it twice shows the same code.</summary>
    [HttpPost("/Audio/Accounts/Invite")]
    public IActionResult OpenInvite()
    {
        return store.Caller(Request) is { } user && store.OpenInvite(user) is { } invite
            ? new JsonResult(Invitation(invite))
            : Api.SignInFirst();
    }

    /// <summary>The caller's live code and who has joined through it — what the inviter's screen polls. Never makes one.</summary>
    [HttpGet("/Audio/Accounts/Invite")]
    public IActionResult CurrentInvite()
    {
        if (store.Caller(Request) is not { } user) return Api.SignInFirst();

        return store.CurrentInvite(user) is { } invite ? new JsonResult(Invitation(invite)) : NotFound();
    }

    /// <summary>Stops the caller's code early.</summary>
    [HttpDelete("/Audio/Accounts/Invite")]
    public IActionResult EndInvite()
    {
        if (store.Caller(Request) is not { } user) return Api.SignInFirst();

        store.EndInvite(user);
        return NoContent();
    }

    /// <summary>
    ///     Who a code belongs to. No token needed: the visitor who scanned it may not have an account
    ///     yet, and should see whose code it is before making one.
    /// </summary>
    [HttpGet("/Audio/Accounts/Invite/{code}")]
    public IActionResult PeekInvite(string code) =>
        store.PeekInvite(code) is { } peek
            ? new JsonResult(new { username = peek.username, expiresUtc = peek.expiresUtc })
            : Api.Error(404, "invalid_code", "That code ran out or was ended. Ask for a new one.");

    [HttpPost("/Audio/Accounts/Invite/Accept")]
    public IActionResult AcceptInvite([FromBody] CodeBody? body)
    {
        if (store.Caller(Request) is not { } user) return Api.SignInFirst();

        var outcome = store.AcceptInvite(user, body?.Code);

        return outcome.Ok
            ? new JsonResult(new { username = outcome.Value.Friend, alreadyFriends = outcome.Value.AlreadyFriends })
            : Api.Refuse(outcome.Refusal);
    }

    private static object Invitation(Invite invite) => new
    {
        code = invite.Code,
        expiresUtc = invite.ExpiresUtc,
        joined = invite.Joined
    };

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

    public sealed record CodeBody(string? Code);
}