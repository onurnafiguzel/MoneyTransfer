namespace MoneyTransfer.Api.Domain;

/// <summary>
/// Logical kind of a ledger transaction. Persisted as a lowercase string
/// ('transfer','deposit','withdrawal','reversal') guarded by a CHECK constraint.
/// </summary>
public enum TransferKind
{
    Transfer,
    Deposit,
    Withdrawal,
    Reversal,
}
