namespace MoneyTransfer.Api.Infrastructure;

/// <summary>Configurable ledger limits bound from the "Ledger" config section.</summary>
public sealed class LedgerOptions
{
    public const string SectionName = "Ledger";

    /// <summary>Upper bound for a single movement amount (minor units). Defaults to the 10^15 CHECK ceiling.</summary>
    public long MaxTransfer { get; set; } = 1_000_000_000_000_000;

    /// <summary>
    /// Currencies for which a system (external) account is seeded.
    /// Default is intentionally EMPTY: the configuration binder APPENDS bound array items onto a
    /// non-empty default (a well-known .NET options gotcha), which would duplicate entries. Config owns this.
    /// </summary>
    public string[] Currencies { get; set; } = [];

    /// <summary>Active concurrency strategy: ConditionalUpdate (default) | Pessimistic | OptimisticCas | Naive.</summary>
    public string ConcurrencyStrategy { get; set; } = "ConditionalUpdate";

    /// <summary>When true, an X-Concurrency-Strategy request header may override the strategy (TEST hook). Keep false in production.</summary>
    public bool AllowStrategyOverride { get; set; }

    /// <summary>Bounded retries for transient contention (serialization/deadlock) and optimistic CAS.</summary>
    public int MaxConcurrencyRetries { get; set; } = 8;
}
