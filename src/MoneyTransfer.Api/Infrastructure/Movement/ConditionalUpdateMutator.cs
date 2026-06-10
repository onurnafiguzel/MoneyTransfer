using Dapper;
using MoneyTransfer.Api.Infrastructure.Persistence;

namespace MoneyTransfer.Api.Infrastructure.Movement;

/// <summary>
/// Strategy B (DEFAULT) — atomic conditional update. The balance invariant lives in the WHERE clause of a
/// guarded UPDATE, so there is no read-modify-write window (no TOCTOU): the right-hand <c>amount</c> is the
/// live, row-locked value, and concurrent movements serialize on that lock. Debit and credit are issued as a
/// SINGLE statement (a CTE) where the credit only fires if the debit produced a row — so it is structurally
/// impossible to credit without debiting (atomicity holds at the statement level, not just the transaction).
///
/// On versioning: <c>version</c> is bumped on every change purely as the audit counter the balance API
/// exposes; it is NOT used as a concurrency token here. A <c>WHERE version = @expected</c> guard would be
/// wrong for B — that is optimistic CAS (Strategy C), which needs it because it reads-then-writes in app code.
/// B never reads-into-app before writing, so it has no stale window and needs no version check.
/// </summary>
public sealed class ConditionalUpdateMutator : IBalanceMutator
{
    public async Task<MutationResult> ApplyAsync(LedgerDbContext db, MovementCommand cmd, CancellationToken ct)
    {
        var (conn, tx) = db.GetDapperContext();

        // Preflight (static facts): existence + currency. These cannot change under our operations, so an
        // unlocked read is sufficient — and it lets us return precise account_not_found / currency_mismatch
        // (which the guarded debit alone could not distinguish from insufficient funds).
        var meta = (await conn.QueryAsync<Meta>(new CommandDefinition(
            "SELECT id AS Id, currency AS Currency, allows_negative AS AllowsNegative FROM accounts WHERE id = ANY(@ids)",
            new { ids = new[] { cmd.FromId, cmd.ToId } }, transaction: tx, cancellationToken: ct))).ToList();
        var from = meta.FirstOrDefault(m => m.Id == cmd.FromId);
        var to = meta.FirstOrDefault(m => m.Id == cmd.ToId);
        if (from is null || to is null) return MutationResult.Fail(MutationStatus.AccountNotFound);
        if (!string.Equals(from.Currency, to.Currency, StringComparison.Ordinal)) return MutationResult.Fail(MutationStatus.CurrencyMismatch);

        // Guarded debit + coupled credit in ONE statement:
        //   - debit's WHERE enforces the no-overdraw invariant against the live locked value (race-safe).
        //   - credit runs only WHERE EXISTS (SELECT 1 FROM debit) → no debit row ⇒ no credit (no money created).
        // Both balances are returned; both NULL ⇒ the guard rejected the debit ⇒ insufficient_funds.
        var row = await conn.QuerySingleAsync<MovementRow>(new CommandDefinition(
            """
            WITH debit AS (
                UPDATE balances SET amount = amount - @amt, version = version + 1, updated_at = now()
                WHERE account_id = @fromId AND (@allowNeg OR amount - @amt >= 0)
                RETURNING amount
            ),
            credit AS (
                UPDATE balances SET amount = amount + @amt, version = version + 1, updated_at = now()
                WHERE account_id = @toId AND EXISTS (SELECT 1 FROM debit)
                RETURNING amount
            )
            SELECT (SELECT amount FROM debit)  AS FromBalanceAfter,
                   (SELECT amount FROM credit) AS ToBalanceAfter
            """,
            new { fromId = cmd.FromId, toId = cmd.ToId, amt = cmd.Amount, allowNeg = from.AllowsNegative },
            transaction: tx, cancellationToken: ct));

        if (row.FromBalanceAfter is null)
            return MutationResult.Fail(MutationStatus.InsufficientFunds);
        if (row.ToBalanceAfter is null)
            // Unreachable in practice (preflight confirmed the destination exists and accounts are never deleted).
            // Throw so the surrounding transaction rolls the debit back rather than committing a half-applied move.
            throw new InvalidOperationException($"Debit applied but credit affected no row for {cmd.ToId}.");

        return MutationResult.Applied(row.FromBalanceAfter.Value, row.ToBalanceAfter.Value);
    }

    private sealed class Meta
    {
        public Guid Id { get; init; }
        public string Currency { get; init; } = "";
        public bool AllowsNegative { get; init; }
    }

    private sealed class MovementRow
    {
        public long? FromBalanceAfter { get; init; }
        public long? ToBalanceAfter { get; init; }
    }
}
