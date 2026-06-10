using Dapper;
using Microsoft.Extensions.Options;
using MoneyTransfer.Api.Infrastructure.Persistence;

namespace MoneyTransfer.Api.Infrastructure.Movement;

/// <summary>
/// Strategy C — optimistic concurrency (compare-and-swap on the version token). Reads amount+version,
/// computes the new balance, then <c>UPDATE … WHERE version = @expected</c>; 0 rows ⇒ a concurrent writer
/// won, so re-read and retry (bounded by <c>MaxConcurrencyRetries</c>). No locks held; best at low contention.
/// </summary>
public sealed class OptimisticCasMutator(IOptions<LedgerOptions> options) : IBalanceMutator
{
    private readonly int _maxRetries = options.Value.MaxConcurrencyRetries;

    public async Task<MutationResult> ApplyAsync(LedgerDbContext db, MovementCommand cmd, CancellationToken ct)
    {
        var (conn, tx) = db.GetDapperContext();

        var meta = (await conn.QueryAsync<Meta>(new CommandDefinition(
            "SELECT id AS Id, currency AS Currency, allows_negative AS AllowsNegative FROM accounts WHERE id = ANY(@ids)",
            new { ids = new[] { cmd.FromId, cmd.ToId } }, transaction: tx, cancellationToken: ct))).ToList();
        var from = meta.FirstOrDefault(m => m.Id == cmd.FromId);
        var to = meta.FirstOrDefault(m => m.Id == cmd.ToId);
        if (from is null || to is null) return MutationResult.Fail(MutationStatus.AccountNotFound);
        if (!string.Equals(from.Currency, to.Currency, StringComparison.Ordinal)) return MutationResult.Fail(MutationStatus.CurrencyMismatch);

        // Debit: the guard can yield a clean insufficient_funds; only a lost CAS triggers a retry.
        long newFrom = 0;
        var debited = false;
        for (var attempt = 0; attempt <= _maxRetries && !debited; attempt++)
        {
            var row = await conn.QuerySingleAsync<VersionedBalance>(new CommandDefinition(
                "SELECT amount AS Amount, version AS Version FROM balances WHERE account_id = @id",
                new { id = cmd.FromId }, transaction: tx, cancellationToken: ct));
            if (!from.AllowsNegative && row.Amount - cmd.Amount < 0) return MutationResult.Fail(MutationStatus.InsufficientFunds);
            newFrom = row.Amount - cmd.Amount;
            var affected = await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE balances SET amount = @newAmount, version = version + 1, updated_at = now() WHERE account_id = @id AND version = @ver",
                new { id = cmd.FromId, newAmount = newFrom, ver = row.Version }, transaction: tx, cancellationToken: ct));
            debited = affected == 1;
        }
        if (!debited) throw new ConcurrencyConflictException($"CAS debit exhausted {_maxRetries} retries for {cmd.FromId}");

        long newTo = 0;
        var credited = false;
        for (var attempt = 0; attempt <= _maxRetries && !credited; attempt++)
        {
            var row = await conn.QuerySingleAsync<VersionedBalance>(new CommandDefinition(
                "SELECT amount AS Amount, version AS Version FROM balances WHERE account_id = @id",
                new { id = cmd.ToId }, transaction: tx, cancellationToken: ct));
            newTo = row.Amount + cmd.Amount;
            var affected = await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE balances SET amount = @newAmount, version = version + 1, updated_at = now() WHERE account_id = @id AND version = @ver",
                new { id = cmd.ToId, newAmount = newTo, ver = row.Version }, transaction: tx, cancellationToken: ct));
            credited = affected == 1;
        }
        if (!credited) throw new ConcurrencyConflictException($"CAS credit exhausted {_maxRetries} retries for {cmd.ToId}");

        return MutationResult.Applied(newFrom, newTo);
    }

    private sealed class Meta
    {
        public Guid Id { get; init; }
        public string Currency { get; init; } = "";
        public bool AllowsNegative { get; init; }
    }

    private sealed class VersionedBalance
    {
        public long Amount { get; init; }
        public long Version { get; init; }
    }
}
