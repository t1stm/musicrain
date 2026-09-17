using JetBrains.Annotations;

namespace Oko;

/// <summary>One action an operator asked for, and what came back.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record AuditEntry(
    DateTimeOffset At,
    string Who,
    string Target,
    string Action,
    string Parameters,
    int Status,
    string? Error);

/// <summary>
///     Every mutation Oko has been asked to make, newest last, capped.
/// </summary>
/// <remarks>
///     Dom's actions destroy real user data that no cache refills, and a delete with no record of who
///     asked for it is the thing you regret. Every attempt is recorded, failures included — a rejected
///     delete is worth seeing.
///     <para>
///         ponytail: in memory, capped at <see cref="Capacity" />, gone on restart. It grows only when
///         a human clicks something, so it cannot run away. Make it an append-only file the day the
///         record has to outlive the process.
///     </para>
/// </remarks>
public sealed class AuditLog
{
    private const int Capacity = 1000;

    private readonly Lock _gate = new();
    private readonly Queue<AuditEntry> _entries = new(Capacity);

    public void Record(AuditEntry entry)
    {
        lock (_gate)
        {
            if (_entries.Count == Capacity) _entries.Dequeue();
            _entries.Enqueue(entry);
        }
    }

    public AuditEntry[] Recent()
    {
        lock (_gate) return _entries.ToArray();
    }

    /// <summary>
    ///     The query string as the log should keep it: every parameter, except that anything named
    ///     like a secret keeps only its name. <c>reset-password</c> carries the new password, and an
    ///     audit log that records it has turned a safety feature into a place passwords accumulate.
    /// </summary>
    public static string Describe(IQueryCollection query)
    {
        return string.Join(" ", query.Select(parameter =>
            Secret(parameter.Key) ? $"{parameter.Key}=***" : $"{parameter.Key}={parameter.Value}"));
    }

    private static bool Secret(string name)
    {
        return name.Contains("password", StringComparison.OrdinalIgnoreCase)
               || name.Contains("token", StringComparison.OrdinalIgnoreCase)
               || name.Contains("secret", StringComparison.OrdinalIgnoreCase);
    }
}
