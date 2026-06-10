namespace MoneyTransfer.Api.Domain;

/// <summary>
/// A single append-only line of the ledger. <see cref="Amount"/> is signed: negative = debit (outflow),
/// positive = credit (inflow). <see cref="BalanceAfter"/> captures the account balance immediately after
/// this entry was applied, giving a self-auditing trail. Fully immutable: created only via the internal
/// factory (used by <see cref="Transfer.AddEntry"/>); UPDATE/DELETE are also blocked by a DB trigger.
/// </summary>
public class LedgerEntry
{
    private LedgerEntry() { } // for EF materialization

    public long Id { get; private init; }
    public Guid TxId { get; private init; }
    public Guid AccountId { get; private init; }

    /// <summary>Signed minor units. CHECK: amount &lt;&gt; 0 AND abs(amount) BETWEEN 1 AND 10^15.</summary>
    public long Amount { get; private init; }

    public long BalanceAfter { get; private init; }
    public DateTimeOffset CreatedAt { get; private init; }

    public Transfer Transfer { get; private set; } = null!;

    internal static LedgerEntry Create(Guid accountId, long amount, long balanceAfter, DateTimeOffset now) =>
        new()
        {
            AccountId = accountId,
            Amount = amount,
            BalanceAfter = balanceAfter,
            CreatedAt = now,
        };
}
