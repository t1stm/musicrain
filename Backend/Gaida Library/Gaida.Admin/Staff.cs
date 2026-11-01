namespace Gaida.Admin;

/// <summary>Who may do what on the admin side. Dom stores it, Oko enforces it, everything else asks Dom.</summary>
/// <remarks>
///     Strings rather than an enum: these names cross the wire, sit in a database column, appear in compose
///     and get compared in Python. An enum would need converting at every one of those edges. See
///     STAFF_PLAN.md for the roles, the permissions and who reaches what.
/// </remarks>
public static class Staff
{
    public const string Owner = "owner";
    public const string Admin = "admin";

    /// <summary>Every permission an Admin can be granted, with the line the Owner's Staff page shows for it.</summary>
    public static readonly IReadOnlyDictionary<string, string> Permissions = new Dictionary<string, string>
    {
        ["accounts"] = "Rename, sign out, reset and delete accounts; rename, hide and delete playlists",
        ["library"] = "Edit songs and covers, rescan the library, import from Deezer",
        ["caches"] = "Evict, forget, promote and demote cached tracks",
        ["rooms"] = "Kick listeners and close rooms",
        ["bot"] = "Set the Discord bot's statuses",
        ["studio"] = "Use the lyrics studio"
    };

    /// <summary>
    ///     Whether a role holding <paramref name="granted" /> may do something that needs <paramref name="needed" />.
    ///     The Owner may do anything. An Admin may only do what was granted, and nothing at all when
    ///     <paramref name="needed" /> is null: an action nobody has named a permission for is the Owner's.
    /// </summary>
    public static bool Can(string? role, IEnumerable<string> granted, string? needed) => role switch
    {
        Owner => true,
        Admin => needed is not null && granted.Contains(needed, StringComparer.Ordinal),
        _ => false
    };

    /// <summary>
    ///     What <c>/Me</c> reports: every permission for the Owner, the known grants for an Admin, none otherwise,
    ///     always in the order <see cref="Permissions" /> lists them. A name dropped from <see cref="Permissions" />
    ///     is therefore revoked everywhere at once, with no migration.
    /// </summary>
    public static string[] Effective(string? role, IEnumerable<string> granted) => role switch
    {
        Owner => [.. Permissions.Keys],
        Admin => [.. Permissions.Keys.Intersect(granted, StringComparer.Ordinal)],
        _ => []
    };
}

/// <summary>The part of Dom's <c>/Audio/Accounts/Me</c> that other services read. Dom sends more; they ignore it.</summary>
/// <param name="Permissions">The effective set, so a client only ever asks whether it contains a name.</param>
public sealed record StaffMe(string Id, string Username, string? Role, string[] Permissions)
{
    public bool IsStaff => Role is Staff.Owner or Staff.Admin;
}
