# Concurrency strategies — side by side

All four implement `IBalanceMutator.ApplyAsync(LedgerDbContext db, MovementCommand cmd, CancellationToken ct)`
and run inside the transaction `LedgerService` opens. The safe three use **Dapper** over the shared
`NpgsqlConnection/NpgsqlTransaction`; `naive` uses EF + the domain methods. Source:
`src/MoneyTransfer.Api/Infrastructure/Movement/`.

The hot-path question is always the same: **how do you debit a balance without a lost update?**

## Naive (⚠ unsafe — demonstration only) — `NaiveMutator.cs`

```csharp
// read via EF, mutate the encapsulated domain, let SaveChanges issue a plain UPDATE (no lock, no version)
if (!from.TryDebit(amount, now)) return InsufficientFunds;
to.Credit(amount, now);
```

Two requests read the same balance and overwrite each other → overspend, and `balance ≠ Σ entries`.
This is the Phase-1 behavior, kept selectable so `k6/01-race-withdrawals.js` can reproduce the bug.

## A — Pessimistic locking — `PessimisticMutator.cs`

```sql
SELECT b.account_id, b.amount, a.currency, a.allows_negative
FROM balances b JOIN accounts a ON a.id = b.account_id
WHERE b.account_id = ANY(@ids)   -- DISTINCT ids
ORDER BY b.account_id            -- deterministic order => no deadlock, no double-lock
FOR UPDATE;                      -- hold the rows for the rest of the txn
-- check invariant in app, then UPDATE both rows
```

- **Use when** a single transaction must hold several balances under a richer rule.
- **Cost** lock hold time → lower throughput on hot rows. No deadlocks (ordered locking).

## B — Atomic conditional update (DEFAULT) — `ConditionalUpdateMutator.cs`

```sql
-- preflight: existence + currency (static under our ops, so unlocked)
SELECT id, currency, allows_negative FROM accounts WHERE id = ANY(@ids);

-- guarded debit: the invariant lives in the WHERE clause — no read-modify-write window
UPDATE balances SET amount = amount - @amt, version = version + 1, updated_at = now()
WHERE account_id = @from AND (@allowNeg OR amount - @amt >= 0)
RETURNING amount;     -- 0 rows => insufficient_funds

UPDATE balances SET amount = amount + @amt, version = version + 1 WHERE account_id = @to RETURNING amount;
```

- **Why default** the core invariant is a single-row predicate; one guarded statement removes the race and
  gives the best throughput on hot accounts.
- **Cost** "0 rows" needs the preflight to distinguish missing-account from insufficient-funds; can deadlock
  on cross-account contention (handled by the retry policy).

## C — Optimistic CAS — `OptimisticCasMutator.cs`

```sql
SELECT amount, version FROM balances WHERE account_id = @id;          -- read
-- compute newAmount in app; guard can yield a clean insufficient_funds
UPDATE balances SET amount = @newAmount, version = version + 1, updated_at = now()
WHERE account_id = @id AND version = @ver;                            -- 0 rows => lost CAS => retry
```

- **Use when** contention is low / reads dominate. No locks held; `version` is the same token the balance API
  returns.
- **Cost** retries waste work under high contention; bounded by `Ledger:MaxConcurrencyRetries`, then escalated
  to the outer retry as a transient conflict.

## Selection & retries

- Active strategy: `Ledger:ConcurrencyStrategy` (config). Clients never choose it; the `X-Concurrency-Strategy`
  header is a test hook gated by `Ledger:AllowStrategyOverride` (dev/compose only). See `BalanceMutatorResolver`.
- `LedgerService` wraps each movement in a bounded, jittered retry for `40001` (serialization), `40P01`
  (deadlock) and CAS exhaustion; every attempt is atomic, so retries are safe.

## Verifying

```bash
docker compose up --build -d
for s in pessimistic conditional optimistic naive; do
  docker compose run --rm -e STRATEGY=$s k6 run /scripts/01-race-withdrawals.js
done
docker compose run --rm -e STRATEGY=conditional k6 run /scripts/03-atomicity-ring.js
```

Safe strategies: exactly M succeed, never negative, `balance == Σ entries`, total conserved. Naive: the run
logs the reproduced overspend / audit break.
