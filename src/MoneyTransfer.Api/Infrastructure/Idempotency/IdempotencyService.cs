using Microsoft.EntityFrameworkCore;
using MoneyTransfer.Api.Infrastructure.Persistence;

namespace MoneyTransfer.Api.Infrastructure.Idempotency;

public enum IdempotencyDecision
{
    /// <summary>No prior transfer for this key — this is the first request; carry on and create it.</summary>
    Proceed,

    /// <summary>A transfer with this key and the SAME content already exists — replay its stored result.</summary>
    Replay,

    /// <summary>A transfer with this key but a DIFFERENT content exists — the key was reused; reject (422).</summary>
    Reuse,

    /// <summary>A concurrent request with this key is still being processed — reject as retryable (409).</summary>
    InProgress,
}

/// <summary>Outcome of the idempotency pre-check. On <see cref="IdempotencyDecision.Replay"/> it carries the
/// original transfer's response fields so the handler can return the first result verbatim.</summary>
public readonly record struct IdempotencyCheck(IdempotencyDecision Decision, Guid TxId, DateTimeOffset CreatedAt)
{
    public static readonly IdempotencyCheck Proceed = new(IdempotencyDecision.Proceed, default, default);
    public static readonly IdempotencyCheck Reuse = new(IdempotencyDecision.Reuse, default, default);
    public static readonly IdempotencyCheck InProgress = new(IdempotencyDecision.InProgress, default, default);
    public static IdempotencyCheck Replay(Guid txId, DateTimeOffset createdAt) =>
        new(IdempotencyDecision.Replay, txId, createdAt);
}

/// <summary>
/// Idempotency coordinator. Two layers, correctness-first:
///   - <b>Redis fast path (Step B)</b>: an in_progress/completed state machine that rejects duplicates and
///     serves replays WITHOUT touching the DB, and answers concurrent copies with a clean 409.
///   - <b>DB backstop (Step A)</b>: the durable record + <c>ux_transfers_idem</c> unique index. It is the
///     source of truth and is consulted whenever Redis can't answer authoritatively (first claim, a Redis
///     flush, a different instance that committed earlier, or Redis being down entirely).
///
/// The handler calls <see cref="BeginAsync"/> before the movement, then exactly one of
/// <see cref="CompleteAsync"/> (on success) or <see cref="ReleaseAsync"/> (on any non-success) after it.
/// </summary>
public sealed class IdempotencyService(LedgerDbContext db, RedisIdempotencyStore redis)
{
    /// <summary>Pre-check + lock claim. Returns Proceed only when this request should run the movement.</summary>
    public async Task<IdempotencyCheck> BeginAsync(string key, string requestHash, CancellationToken ct)
    {
        var fast = await redis.BeginAsync(key, requestHash);
        switch (fast.State)
        {
            case RedisIdemState.Completed: return IdempotencyCheck.Replay(fast.TxId, fast.CreatedAt);
            case RedisIdemState.HashMismatch: return IdempotencyCheck.Reuse;
            case RedisIdemState.InProgress: return IdempotencyCheck.InProgress;
        }

        // Acquired (we hold the Redis lock) or Unavailable (degraded) → consult the durable DB record. This
        // closes the gap where a transfer already exists but Redis doesn't know (flush / other instance / down).
        var durable = await CheckDbAsync(key, requestHash, ct);
        if (durable.Decision == IdempotencyDecision.Replay)
        {
            // A committed transfer already exists; promote Redis to the completed result for fast future replays.
            if (fast.State == RedisIdemState.Acquired)
                await redis.CompleteAsync(key, requestHash, durable.TxId, durable.CreatedAt);
            return durable;
        }
        if (durable.Decision == IdempotencyDecision.Reuse)
        {
            if (fast.State == RedisIdemState.Acquired) await redis.ReleaseAsync(key);
            return IdempotencyCheck.Reuse;
        }
        return IdempotencyCheck.Proceed;
    }

    /// <summary>Cache the successful result so replays are served from Redis. Best-effort; DB stays authoritative.</summary>
    public Task CompleteAsync(string key, string requestHash, Guid txId, DateTimeOffset createdAt) =>
        redis.CompleteAsync(key, requestHash, txId, createdAt);

    /// <summary>Release the in_progress lock after a failed/rejected movement so a corrected retry isn't blocked.</summary>
    public Task ReleaseAsync(string key) => redis.ReleaseAsync(key);

    /// <summary>DB-only durable check (Step A): match a prior transfer by key and compare the persisted hash.</summary>
    private async Task<IdempotencyCheck> CheckDbAsync(string key, string requestHash, CancellationToken ct)
    {
        var existing = await db.Transfers.AsNoTracking()
            .Where(t => t.IdempotencyKey == key)
            .Select(t => new { t.Id, t.RequestHash, t.CreatedAt })
            .FirstOrDefaultAsync(ct);

        if (existing is null) return IdempotencyCheck.Proceed;

        return string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal)
            ? IdempotencyCheck.Replay(existing.Id, existing.CreatedAt)
            : IdempotencyCheck.Reuse;
    }
}
