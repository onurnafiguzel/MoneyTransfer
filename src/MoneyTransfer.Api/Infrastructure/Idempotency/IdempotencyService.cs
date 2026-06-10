using Microsoft.EntityFrameworkCore;
using MoneyTransfer.Api.Infrastructure.Persistence;

namespace MoneyTransfer.Api.Infrastructure.Idempotency;

public enum IdempotencyDecision
{
    /// <summary>No prior transfer for this key — this is the first request; carry on and create it.</summary>
    Proceed,

    /// <summary>A transfer with this key and the SAME content already exists — replay its stored result.</summary>
    Replay,

    /// <summary>A transfer with this key but a DIFFERENT content exists — the key was reused; reject.</summary>
    Reuse,
}

/// <summary>Outcome of the idempotency pre-check. On <see cref="IdempotencyDecision.Replay"/> it carries the
/// stored transfer's response fields so the handler can return the original result verbatim.</summary>
public readonly record struct IdempotencyCheck(IdempotencyDecision Decision, Guid TxId, DateTimeOffset CreatedAt)
{
    public static readonly IdempotencyCheck Proceed = new(IdempotencyDecision.Proceed, default, default);
    public static readonly IdempotencyCheck Reuse = new(IdempotencyDecision.Reuse, default, default);
    public static IdempotencyCheck Replay(Guid txId, DateTimeOffset createdAt) =>
        new(IdempotencyDecision.Replay, txId, createdAt);
}

/// <summary>
/// DB-backed idempotency pre-check (Step A — no Redis yet). Looks up a prior transfer by its Idempotency-Key
/// and compares the persisted request hash:
///   none           → Proceed (first time);
///   same hash       → Replay the stored transfer (client retry / double-submit — no new movement);
///   different hash  → Reuse (same key, different payload → reject).
/// This is the read-side check. The concurrent-insert race (two requests with a brand-new shared key both
/// passing this check) is closed by the <c>ux_transfers_idem</c> unique index in <c>LedgerService</c>, which
/// surfaces the loser as a 409 request_in_progress. A later retry then resolves to Replay here.
/// </summary>
public sealed class IdempotencyService(LedgerDbContext db)
{
    public async Task<IdempotencyCheck> CheckAsync(string key, string requestHash, CancellationToken ct)
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
