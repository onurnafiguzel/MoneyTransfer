namespace MoneyTransfer.Api.Domain;

/// <summary>
/// The header of a logical money movement and the aggregate owning its ledger lines. Encapsulated:
/// created via <see cref="Create"/>, lines appended via <see cref="AddEntry"/>; the <see cref="Entries"/>
/// collection is exposed read-only. Transfers are append-only (immutable): corrections happen via a
/// compensating reversal transfer, never by mutation.
/// </summary>
public class Transfer
{
    private Transfer() { } // for EF materialization

    private readonly List<LedgerEntry> _entries = [];

    public Guid Id { get; private init; }
    public TransferKind Kind { get; private init; }
    public string? Reason { get; private init; }

    /// <summary>If set, this transfer reverses the referenced transfer. Unique: a tx can be reversed at most once.</summary>
    public Guid? ReversedTxId { get; private init; }

    /// <summary>Idempotency key (DB backstop / UNIQUE). Populated by the idempotency feature; null in Phase 1.</summary>
    public string? IdempotencyKey { get; private init; }

    /// <summary>SHA256 hex of the canonical request. Populated by the request-hashing feature; null in Phase 1.</summary>
    public string? RequestHash { get; private init; }

    public DateTimeOffset CreatedAt { get; private init; }

    public IReadOnlyList<LedgerEntry> Entries => _entries;

    public static Transfer Create(
        TransferKind kind, string? reason, Guid? reversedTxId, DateTimeOffset now,
        string? idempotencyKey = null, string? requestHash = null, Guid? id = null) =>
        new()
        {
            Id = id ?? Guid.CreateVersion7(),
            Kind = kind,
            Reason = reason,
            ReversedTxId = reversedTxId,
            IdempotencyKey = idempotencyKey,
            RequestHash = requestHash,
            CreatedAt = now,
        };

    /// <summary>Appends one immutable double-entry line to this transfer.</summary>
    public LedgerEntry AddEntry(Guid accountId, long amount, long balanceAfter, DateTimeOffset now)
    {
        var entry = LedgerEntry.Create(accountId, amount, balanceAfter, now);
        _entries.Add(entry);
        return entry;
    }
}
