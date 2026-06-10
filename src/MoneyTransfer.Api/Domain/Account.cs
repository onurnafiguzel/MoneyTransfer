namespace MoneyTransfer.Api.Domain;

/// <summary>
/// Aggregate root for a single-currency account. State is encapsulated: there are no public setters —
/// instances are produced by the <see cref="Create"/> factory and mutated only through the behavior
/// methods (<see cref="Credit"/> / <see cref="TryDebit"/>), which own the business rules.
///
/// <see cref="IsSystem"/> accounts are the per-currency external counterparties used to keep
/// deposits/withdrawals double-entry balanced; they are allowed to go negative.
/// </summary>
public class Account
{
    private Account() { } // for EF materialization

    public Guid Id { get; private init; }
    public string OwnerRef { get; private init; } = null!;

    /// <summary>ISO-4217 code, normalized to uppercase by the factory (CHECK: currency = upper(currency)).</summary>
    public string Currency { get; private init; } = null!;

    /// <summary>When false, debits that would drive the balance below zero are rejected.</summary>
    public bool AllowsNegative { get; private init; }

    public bool IsSystem { get; private init; }
    public DateTimeOffset CreatedAt { get; private init; }

    public Balance Balance { get; private set; } = null!;

    /// <summary>Factory: creates an account together with its zero balance. Currency is normalized to uppercase.</summary>
    public static Account Create(string ownerRef, string currency, bool allowsNegative, bool isSystem, DateTimeOffset now, Guid? id = null)
    {
        var account = new Account
        {
            Id = id ?? Guid.CreateVersion7(),
            OwnerRef = ownerRef,
            Currency = currency.ToUpperInvariant(),
            AllowsNegative = allowsNegative,
            IsSystem = isSystem,
            CreatedAt = now,
        };
        account.Balance = Balance.Create(account.Id, now);
        return account;
    }

    /// <summary>Adds funds to the balance.</summary>
    public void Credit(long amount, DateTimeOffset now) => Balance.Credit(amount, now);

    /// <summary>
    /// Removes funds if the account policy allows it. Returns false on insufficient funds
    /// (respecting <see cref="AllowsNegative"/>) without mutating state.
    /// </summary>
    public bool TryDebit(long amount, DateTimeOffset now)
    {
        if (!AllowsNegative && Balance.Amount - amount < 0) return false;
        Balance.Debit(amount, now);
        return true;
    }
}
