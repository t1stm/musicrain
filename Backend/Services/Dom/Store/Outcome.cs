using System.Diagnostics.CodeAnalysis;

namespace Dom.Store;

/// <summary>Why the store said no. Each is one error code on the wire — see <see cref="Refusal.Code" />.</summary>
public enum Failure
{
    InvalidRequest,
    UsernameTaken,
    InvalidCredentials,
    Unauthorized,
    NotFound,
    Forbidden,
    Stale,
    NotAFriend,
    InvalidCode,
    OwnCode,
    AlreadyStaff,
    InvalidKey,
    OwnerFixed,
    UnknownPermission,
    NotStaff,
    NoSuchAccount
}

/// <summary>A refusal, and the sentence the client shows for it.</summary>
public sealed record Refusal(Failure Failure, string Message)
{
    /// <summary>The <c>error.code</c> the API answers with.</summary>
    public string Code => Failure switch
    {
        Failure.InvalidRequest => "invalid_request",
        Failure.UsernameTaken => "username_taken",
        Failure.InvalidCredentials => "invalid_credentials",
        Failure.Unauthorized => "unauthorized",
        Failure.NotFound => "not_found",
        Failure.Forbidden => "forbidden",
        Failure.Stale => "stale",
        Failure.NotAFriend => "not_a_friend",
        Failure.InvalidCode => "invalid_code",
        Failure.OwnCode => "own_code",
        Failure.AlreadyStaff => "already_staff",
        Failure.InvalidKey => "invalid_key",
        Failure.OwnerFixed => "owner_fixed",
        Failure.UnknownPermission => "unknown_permission",
        Failure.NotStaff => "not_staff",
        Failure.NoSuchAccount => "no_such_account",
        _ => throw new ArgumentOutOfRangeException(nameof(Failure), Failure, null)
    };
}

/// <summary>What a store operation produced, or the refusal in its place.</summary>
public readonly record struct Outcome<T>(T? Value, Refusal? Refusal) where T : class
{
    [MemberNotNullWhen(true, nameof(Value))]
    [MemberNotNullWhen(false, nameof(Refusal))]
    public bool Ok => Refusal is null;

    public static implicit operator Outcome<T>(T value)
    {
        return new Outcome<T>(value, null);
    }

    public static implicit operator Outcome<T>(Refusal refusal)
    {
        return new Outcome<T>(null, refusal);
    }
}

/// <summary>A signed-in account and the token it was handed.</summary>
public sealed record Session(User User, Token Token);

/// <summary>A playlist after a track was offered to it, and whether the track was new to it.</summary>
public sealed record Appended(Playlist Playlist, bool Added);

/// <summary>Who an accepted code made the caller friends with, and whether they already were.</summary>
public sealed record Friendship(string Friend, bool AlreadyFriends);

/// <summary>An account an operator deleted: its stable ID, the covers left behind, and how many playlists went.</summary>
public sealed record DeletedAccount(string Id, List<string> Covers, int Playlists);

/// <summary>One staff account as the Owner's Staff page lists it. <c>Permissions</c> is the effective set.</summary>
public sealed record StaffMember(string Username, string Role, string[] Permissions);
