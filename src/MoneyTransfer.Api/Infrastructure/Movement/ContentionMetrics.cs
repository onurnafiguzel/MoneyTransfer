using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MoneyTransfer.Api.Infrastructure.Movement;

/// <summary>Point-in-time view of contention counters (cumulative since process start).</summary>
public readonly record struct ContentionSnapshot(
    long TransientEvents,
    long Deadlocks,
    long SerializationFailures,
    long CasConflicts,
    long Exhaustions,
    long RecoveredAfterRetry);

/// <summary>
/// Process-wide counters that make concurrency contention OBSERVABLE rather than only logged — so a load
/// test can prove that deadlocks/serialization failures actually occurred AND were transparently recovered
/// (vs. silently never happening). Thread-safe via <see cref="Interlocked"/>; registered as a singleton.
///
/// - <c>TransientEvents</c>: every transient failure caught by the retry loop (sum of the typed ones below + any other).
/// - <c>Deadlocks</c> / <c>SerializationFailures</c> / <c>CasConflicts</c>: typed breakdown (40P01 / 40001 / optimistic CAS).
/// - <c>Exhaustions</c>: movements that gave up after the retry budget → returned a retryable 503 (the failure mode to watch).
/// - <c>RecoveredAfterRetry</c>: movements that completed after ≥1 retry → contention overcome (the success story).
/// </summary>
public sealed class ContentionMetrics
{
    private long _transient, _deadlocks, _serialization, _cas, _exhaustions, _recovered;

    /// <summary>Record one transient failure encountered by the retry loop, classified by its SQL state / type.</summary>
    public void RecordTransient(Exception ex)
    {
        Interlocked.Increment(ref _transient);
        switch (SqlStateOf(ex), ex)
        {
            case (PostgresErrorCodes.DeadlockDetected, _): Interlocked.Increment(ref _deadlocks); break;
            case (PostgresErrorCodes.SerializationFailure, _): Interlocked.Increment(ref _serialization); break;
            case (_, ConcurrencyConflictException): Interlocked.Increment(ref _cas); break;
        }
    }

    /// <summary>Record a movement that exhausted its retry budget (surfaced as 503 too_much_contention).</summary>
    public void RecordExhaustion() => Interlocked.Increment(ref _exhaustions);

    /// <summary>Record a movement that completed after at least one retry — contention was overcome.</summary>
    public void RecordRecovery() => Interlocked.Increment(ref _recovered);

    public ContentionSnapshot Snapshot() => new(
        Interlocked.Read(ref _transient),
        Interlocked.Read(ref _deadlocks),
        Interlocked.Read(ref _serialization),
        Interlocked.Read(ref _cas),
        Interlocked.Read(ref _exhaustions),
        Interlocked.Read(ref _recovered));

    private static string? SqlStateOf(Exception ex) => ex switch
    {
        PostgresException pg => pg.SqlState,
        DbUpdateException { InnerException: PostgresException inner } => inner.SqlState,
        _ => null,
    };
}
