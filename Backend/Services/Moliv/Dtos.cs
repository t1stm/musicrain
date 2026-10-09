using JetBrains.Annotations;

namespace Moliv;

// ── What the client sends ───────────────────────────────────────────────────────────────────────

/// <summary>
///     The body of <c>PUT /Audio/History/Plays/{id}</c>, every field nullable so a missing one is a
///     readable 400 rather than a default that quietly lands in the table.
/// </summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record PlayBody(
    string? TrackId,
    DateTimeOffset? StartedUtc,
    int? UtcOffsetMinutes,
    long? DurationMs,
    long? PlayedMs,
    string? StartReason,
    string? EndReason,
    string? SourceKind,
    string? SourceId,
    string? SessionId,
    int? SessionPosition,
    string? Platform,
    string? DeviceKind,
    bool? Shuffled)
{
    private static readonly HashSet<string> StartReasons = ["chosen", "collection", "autoplay", "next", "previous", "room"];
    private static readonly HashSet<string> EndReasons = ["finished", "skipped", "previous", "replaced", "stopped", "error"];

    private static readonly HashSet<string> SourceKinds =
        ["search", "album", "artist", "playlist", "browse", "home-roll", "recent", "history", "link", "queue", "room"];

    private static readonly HashSet<string> Platforms = ["web", "pwa", "discord"];
    private static readonly HashSet<string> DeviceKinds = ["mobile", "desktop"];

    /// <summary>
    ///     The row this body writes, or why it will not. The trust boundary: nothing here is believed
    ///     because the client sent it.
    /// </summary>
    public (PlayRow? row, string? error) ToRow(string id, Caller caller, DateTimeOffset now)
    {
        if (TrackId is not { Length: > 0 and <= 512 } track || !track.Contains("://", StringComparison.Ordinal))
            return (null, "trackId must look like audio://… and be at most 512 characters.");
        // The outbox's horizon (HISTORY_PLAN.md §B1): nothing older is still waiting to be sent.
        if (StartedUtc is not { } started || started > now.AddMinutes(5) || started < now.AddDays(-30))
            return (null, "startedUtc must be within the last 30 days.");
        if (UtcOffsetMinutes is not (>= -14 * 60 and <= 14 * 60) || SessionPosition is not >= 0 ||
            DurationMs is not >= 0 || PlayedMs is not >= 0)
            return (null, "utcOffsetMinutes, sessionPosition, durationMs and playedMs are required, and not negative.");
        if (StartReason is null || !StartReasons.Contains(StartReason) ||
            (EndReason is not null && !EndReasons.Contains(EndReason)))
            return (null, "startReason or endReason is not one this service knows.");
        if (SourceKind is null || !SourceKinds.Contains(SourceKind) || SourceId is { Length: > 512 })
            return (null, "sourceKind is not one this service knows, or sourceId is too long.");
        if (Platform is null || !Platforms.Contains(Platform) || DeviceKind is null || !DeviceKinds.Contains(DeviceKind))
            return (null, "platform or deviceKind is not one this service knows.");
        if (!Guid.TryParseExact(SessionId, "D", out var session) || Shuffled is null)
            return (null, "sessionId must be a UUID, and shuffled is required.");

        // Clamped, not refused: a clock that ran a little long is not worth losing the play over. A track
        // with no known duration sends 0, and there is nothing to clamp against.
        var played = DurationMs > 0 ? Math.Min(PlayedMs.Value, DurationMs.Value + 5000) : PlayedMs.Value;

        return (new PlayRow(id, caller.UserId, caller.DeviceId, track, started.ToUniversalTime(),
            UtcOffsetMinutes.Value, DurationMs.Value, played, StartReason, EndReason, SourceKind, SourceId,
            session.ToString(), SessionPosition.Value, Platform, DeviceKind, Shuffled.Value, now), null);
    }
}

// ── What the client gets back ───────────────────────────────────────────────────────────────────

/// <summary>One row of <c>GET /Audio/History</c>.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record PlayDto(
    string Id,
    string TrackId,
    DateTimeOffset StartedUtc,
    long PlayedMs,
    long DurationMs,
    string? EndReason);

/// <summary>A page of <c>GET /Audio/History</c>. <c>Next</c> is opaque to the client, and <c>null</c> on the last page.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record PageDto(IReadOnlyList<PlayDto> Plays, string? Next);

/// <summary>One row of <c>GET /Audio/History/Recent</c>: a track, and the last time it was played.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record RecentDto(string TrackId, DateTimeOffset StartedUtc);

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record ApiError(string Code, string Message);

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record ApiErrorBody(ApiError Error);

public static class Api
{
    /// <summary>The stack's error envelope: <c>{"error":{"code":"…","message":"…"}}</c>.</summary>
    public static IResult Error(int status, string code, string message) =>
        Results.Json(new ApiErrorBody(new ApiError(code, message)), statusCode: status);

    /// <summary>
    ///     A page cursor: the last row's <c>StartedUtc|Id</c>, compared as a row value. Round-trip format,
    ///     so the instant parsed back is the instant written and the comparison is exact.
    /// </summary>
    public static string Cursor(PlayDto play) => $"{play.StartedUtc.ToUniversalTime():O}|{play.Id}";

    public static (DateTimeOffset startedUtc, string id)? ParseCursor(string cursor)
    {
        var bar = cursor.LastIndexOf('|');
        return bar > 0 && DateTimeOffset.TryParse(cursor[..bar], null,
            System.Globalization.DateTimeStyles.RoundtripKind, out var started)
            ? (started.ToUniversalTime(), cursor[(bar + 1)..])
            : null;
    }
}
