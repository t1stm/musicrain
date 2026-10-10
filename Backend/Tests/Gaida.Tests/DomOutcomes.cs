using Dom.Store;

namespace Gaida.Tests;

/// <summary>The store's outcomes read back as the tuples these tests were written against: value, code, message.</summary>
internal static class DomOutcomes
{
    public static void Deconstruct(this Outcome<Session> outcome, out Token? token, out User? user, out string? error,
        out string? message)
    {
        (token, user, error, message) =
            (outcome.Value?.Token, outcome.Value?.User, outcome.Refusal?.Code, outcome.Refusal?.Message);
    }

    public static void Deconstruct(this Outcome<Playlist> outcome, out Playlist? playlist, out string? error,
        out string? message)
    {
        (playlist, error, message) = (outcome.Value, outcome.Refusal?.Code, outcome.Refusal?.Message);
    }

    public static void Deconstruct(this Outcome<Appended> outcome, out Playlist? playlist, out bool added,
        out string? error, out string? message)
    {
        (playlist, added, error, message) = (outcome.Value?.Playlist, outcome.Value?.Added ?? false,
            outcome.Refusal?.Code, outcome.Refusal?.Message);
    }

    public static void Deconstruct(this Outcome<Friendship> outcome, out string? friend, out bool alreadyFriends,
        out string? error, out string? message)
    {
        (friend, alreadyFriends, error, message) = (outcome.Value?.Friend, outcome.Value?.AlreadyFriends ?? false,
            outcome.Refusal?.Code, outcome.Refusal?.Message);
    }

    public static void Deconstruct(this Outcome<Token> outcome, out Token? token, out string? error,
        out string? message)
    {
        (token, error, message) = (outcome.Value, outcome.Refusal?.Code, outcome.Refusal?.Message);
    }

    public static void Deconstruct(this Outcome<List<string>> outcome, out string? error, out string? message,
        out List<string> covers)
    {
        (error, message, covers) = (outcome.Refusal?.Code, outcome.Refusal?.Message, outcome.Value ?? []);
    }
}
