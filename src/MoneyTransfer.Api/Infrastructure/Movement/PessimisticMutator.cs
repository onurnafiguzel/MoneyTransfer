using Dapper;
using MoneyTransfer.Api.Infrastructure.Persistence;

namespace MoneyTransfer.Api.Infrastructure.Movement;

/// <summary>
/// Strategy A — pessimistic locking. Locks both balance rows with <c>SELECT … FOR UPDATE</c> in a
/// deterministic order (DISTINCT ids + ORDER BY account_id → no deadlock, no double-lock of the same row),
/// checks the invariant in app code, then updates. Strong and simple; holds locks for the transaction.
/// </summary>
public sealed class PessimisticMutator : IBalanceMutator
{
    public async Task<MutationResult> ApplyAsync(LedgerDbContext db, MovementCommand cmd, CancellationToken ct)
    {
        var (conn, tx) = db.GetDapperContext();
        var ids = new[] { cmd.FromId, cmd.ToId }.Distinct().ToArray();

        var locked = (await conn.QueryAsync<Row>(new CommandDefinition(
            """
            SELECT b.account_id AS AccountId, b.amount AS Amount, a.currency AS Currency, a.allows_negative AS AllowsNegative
            FROM balances b JOIN accounts a ON a.id = b.account_id
            WHERE b.account_id = ANY(@ids)
            ORDER BY b.account_id
            FOR UPDATE
            """, new { ids }, transaction: tx, cancellationToken: ct))).ToList();

        var from = locked.FirstOrDefault(r => r.AccountId == cmd.FromId);
        var to = locked.FirstOrDefault(r => r.AccountId == cmd.ToId);
        if (from is null || to is null) return MutationResult.Fail(MutationStatus.AccountNotFound);
        if (!string.Equals(from.Currency, to.Currency, StringComparison.Ordinal)) return MutationResult.Fail(MutationStatus.CurrencyMismatch);
        if (!from.AllowsNegative && from.Amount - cmd.Amount < 0) return MutationResult.Fail(MutationStatus.InsufficientFunds);

        var newFrom = from.Amount - cmd.Amount;
        var newTo = to.Amount + cmd.Amount;
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE balances SET amount = @amount, version = version + 1, updated_at = now() WHERE account_id = @id",
            new[] { new { id = cmd.FromId, amount = newFrom }, new { id = cmd.ToId, amount = newTo } },
            transaction: tx, cancellationToken: ct));

        return MutationResult.Applied(newFrom, newTo);
    }

    private sealed class Row
    {
        public Guid AccountId { get; init; }
        public long Amount { get; init; }
        public string Currency { get; init; } = "";
        public bool AllowsNegative { get; init; }
    }
}
