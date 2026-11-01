using Dom.Store;
using Gaida.Admin;
using Microsoft.AspNetCore.Mvc;

namespace Dom.Controllers;

/// <summary>
///     Who among the accounts is staff. Claiming the administrator key is open to any signed-in account;
///     everything else is the Owner's. Not behind Oko: these need the caller's identity, and Dom has it
///     without asking anyone. See STAFF_PLAN.md.
/// </summary>
/// <remarks>
///     <c>POST /Staff/Claim</c> and <c>PUT /Staff/{username}</c> differ by method, so an account called
///     "Claim" can never shadow the route — the same trick <see cref="Accounts" /> relies on for <c>Invite</c>.
/// </remarks>
public class StaffAccounts(DomStore store) : ControllerBase
{
    [HttpPost("/Audio/Accounts/Staff/Claim")]
    public IActionResult Claim([FromBody] KeyBody? body)
    {
        if (store.Caller(Request) is not { } user) return Api.SignInFirst();

        var outcome = store.ClaimStaff(user, body?.Key);
        return outcome.Ok
            ? new JsonResult(new { role = outcome.Value.Role, permissions = outcome.Value.Permissions })
            : Api.Refuse(outcome.Refusal);
    }

    /// <summary>
    ///     The staff, and the vocabulary with the line the Staff page shows for each permission, so the
    ///     frontend keeps no copy of it. <c>keyWaiting</c> tells the Owner an unclaimed key is lying around.
    /// </summary>
    [HttpGet("/Audio/Accounts/Staff")]
    public IActionResult List()
    {
        if (store.Caller(Request) is not { } user) return Api.SignInFirst();

        var outcome = store.StaffList(user);
        return outcome.Ok
            ? new JsonResult(new
            {
                permissions = Staff.Permissions.Select(permission =>
                    new { name = permission.Key, description = permission.Value }),
                staff = outcome.Value.Select(Shown),
                keyWaiting = store.KeyWaiting
            })
            : Api.Refuse(outcome.Refusal);
    }

    /// <summary>Promotes and edits alike: an account with no role becomes an Admin with exactly these grants.</summary>
    [HttpPut("/Audio/Accounts/Staff/{username}")]
    public IActionResult Grant(string username, [FromBody] Grants? body)
    {
        if (store.Caller(Request) is not { } user) return Api.SignInFirst();

        var outcome = store.GrantStaff(user, username, body?.Permissions);
        return outcome.Ok ? new JsonResult(Shown(outcome.Value)) : Api.Refuse(outcome.Refusal);
    }

    [HttpDelete("/Audio/Accounts/Staff/{username}")]
    public IActionResult Demote(string username)
    {
        if (store.Caller(Request) is not { } user) return Api.SignInFirst();

        return store.DemoteStaff(user, username) is { } refusal ? Api.Refuse(refusal) : NoContent();
    }

    private static object Shown(StaffMember member) =>
        new { username = member.Username, role = member.Role, permissions = member.Permissions };

    public sealed record KeyBody(string? Key);

    public sealed record Grants(List<string>? Permissions);
}
