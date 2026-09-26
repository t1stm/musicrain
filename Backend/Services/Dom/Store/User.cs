using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using JetBrains.Annotations;

namespace Dom.Store;

/// <summary>
///     One account. The password is never held — only the PBKDF2 output and the parameters it was
///     produced with, so the cost can be raised later without invalidating everybody's password.
/// </summary>
public sealed class User
{
    /// <summary>As typed. <see cref="Key" /> is what uniqueness is decided on.</summary>
    public required string Username { get; set; }

    public required string Salt { get; set; }
    public required string Hash { get; set; }
    public required int Iterations { get; set; }

    public DateTimeOffset CreatedUtc { get; init; }
    /// <summary>
    ///     <c>init</c>, not get-only: System.Text.Json skips a property it cannot set, so a get-only list
    ///     was written to the file and never read back — every restart signed everybody out.
    /// </summary>
    public List<Token> Tokens { get; init; } = [];

    /// <summary>
    ///     The frontend's preferences, as it sent them. Opaque here on purpose: a new preference is a
    ///     frontend change, and the frontend validates every value it reads back. <c>null</c> until the
    ///     account first saves any, which the client needs to tell apart from an empty object.
    /// </summary>
    public JsonObject? Settings { get; set; }

    public DateTimeOffset? SettingsUpdatedUtc { get; set; }

    /// <summary>Two accounts may not differ only by case. Derived, so it is not written to the file.</summary>
    [JsonIgnore]
    public string Key => Normalize(Username);

    public static string Normalize(string username) => username.Trim().ToLowerInvariant();
}

/// <summary>A bearer token and the moment it stops working.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class Token
{
    public required string Value { get; set; }
    public DateTimeOffset IssuedUtc { get; set; }
    public DateTimeOffset ExpiresUtc { get; set; }
}

/// <summary>The whole file. Versioned so a later shape can be migrated rather than guessed at.</summary>
public sealed class DomState
{
    public int Version { get; init; } = 1;
    public List<User> Users { get; init; } = [];
    public List<Playlist> Playlists { get; init; } = [];
}
