namespace MoneyTransfer.Api.Domain;

/// <summary>
/// Fast-read projection of an account's current balance (minor units / "cents"). Part of the
/// <see cref="Account"/> aggregate: it is created and mutated only via <see cref="Account"/>
/// (its mutators are <c>internal</c>), so the balance rules live in one place.
///
/// <see cref="Version"/> is a monotonically increasing counter exposed by the balance API.
/// NOTE (Phase 1): Version is NOT yet a concurrency token — the naive read-modify-write at the
/// service layer is intentionally race-prone; optimistic CAS using this column is a later feature.
/// </summary>
public class Balance
{
    private Balance() { } // for EF materialization

    public Guid AccountId { get; private init; }

    /// <summary>Current balance in minor units. May be negative only when the owning account AllowsNegative.</summary>
    public long Amount { get; private set; }

    public long Version { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public Account Account { get; private set; } = null!;

    internal static Balance Create(Guid accountId, DateTimeOffset now) =>
        new() { AccountId = accountId, Amount = 0, Version = 0, UpdatedAt = now };

    internal void Credit(long amount, DateTimeOffset now)
    {
        Amount += amount;
        Version++;
        UpdatedAt = now;
    }

    internal void Debit(long amount, DateTimeOffset now)
    {
        Amount -= amount;
        Version++;
        UpdatedAt = now;
    }
}
