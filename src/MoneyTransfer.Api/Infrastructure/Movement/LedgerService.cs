using Microsoft.EntityFrameworkCore;
using MoneyTransfer.Api.Domain;
using MoneyTransfer.Api.Infrastructure.Persistence;
using Npgsql;

namespace MoneyTransfer.Api.Infrastructure.Movement;

public enum MovementError { None, AccountNotFound, CurrencyMismatch, InsufficientFunds }

public enum ReverseError { None, NotFound, AlreadyReversed, InsufficientFunds, AccountNotFound }

/// <summary>
/// NAIVE Phase-1 money-movement engine. It loads balances, checks invariants in app code, then writes
/// — all in a single <c>SaveChanges</c>, i.e. one DB transaction, so a movement is atomic (all-or-nothing).
///
/// What it deliberately does NOT do yet: protect the read-modify-write against concurrency. Two requests
/// can both read the same balance and overwrite each other (lost update / overspend). Hardening this race
/// (pessimistic locking / atomic conditional update / optimistic CAS) is a dedicated later feature, where
/// this single class becomes the one place that changes.
/// </summary>
public sealed class LedgerService(LedgerDbContext db)
{
    /// <summary>Moves <paramref name="amount"/> minor units from one account to another within one transaction.</summary>
    public async Task<(Transfer? Transfer, MovementError Error)> MoveAsync(
        Guid fromId,
        Guid toId, 
        long amount, 
        TransferKind kind,
        string? reason,
        Guid? reversedTxId, CancellationToken ct)
    {
        var from = await db.Accounts.Include(a => a.Balance).FirstOrDefaultAsync(a => a.Id == fromId, ct);
        var to = await db.Accounts.Include(a => a.Balance).FirstOrDefaultAsync(a => a.Id == toId, ct);

        if (from is null || to is null) return (null, MovementError.AccountNotFound);
        if (!string.Equals(from.Currency, to.Currency, StringComparison.Ordinal)) return (null, MovementError.CurrencyMismatch);

        var now = DateTimeOffset.UtcNow;
        // Domain methods own the balance rules (encapsulated). TryDebit respects AllowsNegative.
        if (!from.TryDebit(amount, now)) return (null, MovementError.InsufficientFunds);
        to.Credit(amount, now);

        var tx = Transfer.Create(kind, reason, reversedTxId, now);
        // Double-entry: debit (source, negative) + credit (destination, positive) sum to zero.
        tx.AddEntry(from.Id, -amount, from.Balance.Amount, now);
        tx.AddEntry(to.Id, amount, to.Balance.Amount, now);
        db.Transfers.Add(tx);

        await db.SaveChangesAsync(ct); // single transaction => atomic
        return (tx, MovementError.None);
    }

    /// <summary>Deposit: moves funds from this currency's system (external) account into the user account.</summary>
    public async Task<(Transfer? Transfer, MovementError Error)> DepositAsync(Guid accountId, long amount, string? reason, CancellationToken ct)
    {
        var system = await ResolveSystemCounterpartyAsync(accountId, ct);
        if (system is null) return (null, MovementError.AccountNotFound);
        return await MoveAsync(system.Value, accountId, amount, TransferKind.Deposit, reason, null, ct);
    }

    /// <summary>Withdrawal: moves funds from the user account back into the system (external) account.</summary>
    public async Task<(Transfer? Transfer, MovementError Error)> WithdrawAsync(Guid accountId, long amount, string? reason, CancellationToken ct)
    {
        var system = await ResolveSystemCounterpartyAsync(accountId, ct);
        if (system is null) return (null, MovementError.AccountNotFound);
        return await MoveAsync(accountId, system.Value, amount, TransferKind.Withdrawal, reason, null, ct);
    }

    /// <summary>
    /// Reverses a transfer by creating a compensating movement (a new, immutable transfer).
    /// A transfer can be reversed at most once (guarded in app + by the ux_transfers_reversed_once unique index).
    /// Reversing a reversal is allowed (it is just another transfer that hasn't been reversed yet).
    /// </summary>
    public async Task<(Transfer? Reversal, ReverseError Error)> ReverseAsync(Guid txId, string? reason, CancellationToken ct)
    {
        var original = await db.Transfers.Include(t => t.Entries).FirstOrDefaultAsync(t => t.Id == txId, ct);
        if (original is null) return (null, ReverseError.NotFound);
        if (await db.Transfers.AnyAsync(t => t.ReversedTxId == txId, ct)) return (null, ReverseError.AlreadyReversed);

        // Original debit (Amount < 0) = source; credit (Amount > 0) = destination. The reversal swaps them.
        var debit = original.Entries.First(en => en.Amount < 0);
        var credit = original.Entries.First(en => en.Amount > 0);
        var amount = credit.Amount; // positive magnitude

        try
        {
            var (reversal, error) = await MoveAsync(credit.AccountId, debit.AccountId, amount, TransferKind.Reversal, reason, txId, ct);
            return error switch
            {
                MovementError.None => (reversal, ReverseError.None),
                MovementError.InsufficientFunds => (null, ReverseError.InsufficientFunds),
                _ => (null, ReverseError.AccountNotFound),
            };
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A concurrent reversal slipped past the AnyAsync check; the unique index is the backstop.
            return (null, ReverseError.AlreadyReversed);
        }
    }

    private async Task<Guid?> ResolveSystemCounterpartyAsync(Guid accountId, CancellationToken ct)
    {
        var currency = await db.Accounts.Where(a => a.Id == accountId).Select(a => a.Currency).FirstOrDefaultAsync(ct);
        if (currency is null) return null;
        return await db.Accounts
            .Where(a => a.IsSystem && a.Currency == currency)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefaultAsync(ct);
    }
}
