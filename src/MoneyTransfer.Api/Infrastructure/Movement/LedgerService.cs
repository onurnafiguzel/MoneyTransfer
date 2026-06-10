using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MoneyTransfer.Api.Domain;
using MoneyTransfer.Api.Infrastructure.Persistence;
using Npgsql;

namespace MoneyTransfer.Api.Infrastructure.Movement;

public enum MovementError { None, AccountNotFound, CurrencyMismatch, InsufficientFunds }

public enum ReverseError { None, NotFound, AlreadyReversed, InsufficientFunds, AccountNotFound }

/// <summary>
/// Money-movement engine. Each movement runs in a single DB transaction (atomic). The concurrency-critical
/// balance mutation is delegated to the configured <see cref="IBalanceMutator"/> strategy (Dapper SQL shares
/// the EF transaction); LedgerService then writes the immutable double-entry transfer and commits. Transient
/// DB contention (serialization 40001 / deadlock 40P01) and optimistic-CAS exhaustion are retried with backoff.
/// </summary>
public sealed class LedgerService(
    LedgerDbContext db,
    IBalanceMutatorResolver mutators,
    IOptions<LedgerOptions> options,
    ILogger<LedgerService> logger)
{
    private readonly int _maxRetries = options.Value.MaxConcurrencyRetries;

    public async Task<(Transfer? Transfer, MovementError Error)> MoveAsync(
        Guid fromId, Guid toId, long amount, TransferKind kind, string? reason, Guid? reversedTxId, CancellationToken ct)
    {
        var mutator = mutators.Resolve();
        var cmd = new MovementCommand(fromId, toId, amount);

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await ExecuteOnceAsync(mutator, cmd, kind, reason, reversedTxId, ct);
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < _maxRetries)
            {
                db.ChangeTracker.Clear(); // discard tracked state from the rolled-back attempt
                logger.LogWarning("Movement contention (attempt {Attempt}/{Max}): {Error}; retrying", attempt + 1, _maxRetries, ex.GetType().Name);
                await Task.Delay(BackoffMs(attempt), ct);
            }
        }
    }

    private async Task<(Transfer?, MovementError)> ExecuteOnceAsync(
        IBalanceMutator mutator, MovementCommand cmd, TransferKind kind, string? reason, Guid? reversedTxId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var result = await mutator.ApplyAsync(db, cmd, ct);
        if (result.Status != MutationStatus.Applied)
        {
            await tx.RollbackAsync(ct);
            return (null, MapError(result.Status));
        }

        var transfer = Transfer.Create(kind, reason, reversedTxId, DateTimeOffset.UtcNow);
        transfer.AddEntry(cmd.FromId, -cmd.Amount, result.FromBalanceAfter, transfer.CreatedAt);
        transfer.AddEntry(cmd.ToId, cmd.Amount, result.ToBalanceAfter, transfer.CreatedAt);
        db.Transfers.Add(transfer);

        await db.SaveChangesAsync(ct);   // inserts transfer + entries within the same transaction
        await tx.CommitAsync(ct);
        return (transfer, MovementError.None);
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
    /// Reverses a transfer by creating a compensating movement (a new, immutable transfer). A transfer can be
    /// reversed at most once (guarded in app + by the ux_transfers_reversed_once unique index). Reversing a
    /// reversal is allowed (it is just another transfer that hasn't been reversed yet).
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

    private static MovementError MapError(MutationStatus status) => status switch
    {
        MutationStatus.InsufficientFunds => MovementError.InsufficientFunds,
        MutationStatus.CurrencyMismatch => MovementError.CurrencyMismatch,
        _ => MovementError.AccountNotFound,
    };

    private static bool IsTransient(Exception ex) =>
        ex is ConcurrencyConflictException
        || (ex is PostgresException pg && IsTransientSqlState(pg.SqlState))
        || (ex is DbUpdateException { InnerException: PostgresException inner } && IsTransientSqlState(inner.SqlState));

    private static bool IsTransientSqlState(string? sqlState) =>
        sqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected;

    private static int BackoffMs(int attempt) => (int)(Math.Pow(2, attempt) * 5) + Random.Shared.Next(0, 15);
}
