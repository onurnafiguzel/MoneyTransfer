using MoneyTransfer.Api.Infrastructure.Persistence;

namespace MoneyTransfer.Api.Infrastructure.Movement;

/// <summary>A request to move <see cref="Amount"/> minor units from one account to another.</summary>
public readonly record struct MovementCommand(Guid FromId, Guid ToId, long Amount);

public enum MutationStatus { Applied, InsufficientFunds, AccountNotFound, CurrencyMismatch }

/// <summary>Outcome of a balance mutation, including the resulting balances for the ledger entries' balance_after.</summary>
public readonly record struct MutationResult(MutationStatus Status, long FromBalanceAfter, long ToBalanceAfter)
{
    public static MutationResult Applied(long fromBalanceAfter, long toBalanceAfter) =>
        new(MutationStatus.Applied, fromBalanceAfter, toBalanceAfter);

    public static MutationResult Fail(MutationStatus status) => new(status, 0, 0);
}

/// <summary>
/// Strategy abstraction for the concurrency-critical balance mutation. Implementations differ only in HOW
/// they keep the debit safe under concurrent access (naive / pessimistic / conditional / optimistic). Each
/// runs inside the transaction <c>LedgerService</c> opens and reports the resulting balances.
/// </summary>
public interface IBalanceMutator
{
    Task<MutationResult> ApplyAsync(LedgerDbContext db, MovementCommand cmd, CancellationToken ct);
}

/// <summary>Selects the active mutator: the configured default, with an optional env-gated test override.</summary>
public interface IBalanceMutatorResolver
{
    IBalanceMutator Resolve();
}

/// <summary>Raised when optimistic CAS cannot converge within its retry budget; treated as transient (outer retry).</summary>
public sealed class ConcurrencyConflictException(string message) : Exception(message);
