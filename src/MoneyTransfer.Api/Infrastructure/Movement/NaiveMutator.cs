using Microsoft.EntityFrameworkCore;
using MoneyTransfer.Api.Infrastructure.Persistence;

namespace MoneyTransfer.Api.Infrastructure.Movement;

/// <summary>
/// ⚠ UNSAFE — demonstration only. The original Phase-1 path: read balances via EF, mutate the encapsulated
/// domain (<c>TryDebit</c>/<c>Credit</c>), and let <c>LedgerService.SaveChanges</c> persist a plain UPDATE
/// (no row lock, no version guard). Under concurrency this loses updates / overspends and breaks the
/// self-audit invariant (balance ≠ Σ entries). Selectable so the race is reproducible; never deploy.
/// </summary>
public sealed class NaiveMutator : IBalanceMutator
{
    public async Task<MutationResult> ApplyAsync(LedgerDbContext db, MovementCommand cmd, CancellationToken ct)
    {
        var from = await db.Accounts.Include(a => a.Balance).FirstOrDefaultAsync(a => a.Id == cmd.FromId, ct);
        var to = await db.Accounts.Include(a => a.Balance).FirstOrDefaultAsync(a => a.Id == cmd.ToId, ct);
        if (from is null || to is null) return MutationResult.Fail(MutationStatus.AccountNotFound);
        if (!string.Equals(from.Currency, to.Currency, StringComparison.Ordinal)) return MutationResult.Fail(MutationStatus.CurrencyMismatch);

        var now = DateTimeOffset.UtcNow;
        if (!from.TryDebit(cmd.Amount, now)) return MutationResult.Fail(MutationStatus.InsufficientFunds);
        to.Credit(cmd.Amount, now);
        // Tracked changes are flushed by LedgerService.SaveChanges within the shared transaction.
        return MutationResult.Applied(from.Balance.Amount, to.Balance.Amount);
    }
}
