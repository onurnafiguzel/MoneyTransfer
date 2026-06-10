# ADR-0001 — Concurrency strategy for balance mutation

- **Status:** Accepted
- **Date:** 2026-06-10
- **Context feature:** Race conditions (problem → approach → implementation → verification)

## Context

A withdrawal/transfer must never let an account's balance go below zero (unless `allows_negative`),
and the ledger must stay self-auditing: `balance == Σ(ledger entries)` for every account, and the global
sum of balances must be conserved. The Phase-1 engine read the balance, computed in app code, then wrote
it back. Under concurrent withdrawals on the same account this read-modify-write loses updates and
overspends — money is created from nothing and the self-audit breaks. (Reproduced by `k6/01-race-withdrawals.js`
with the `naive` strategy.)

The balance mutation is the only concurrency-critical step, so we isolate it behind one abstraction and
choose a correct, high-throughput implementation — evaluating the standard options rather than assuming one.

## Options considered

All implement `IBalanceMutator` (`src/MoneyTransfer.Api/Infrastructure/Movement/`) and run inside the single
DB transaction `LedgerService` opens; the concurrency-critical SQL is written explicitly with **Dapper**
sharing the EF connection/transaction (hybrid data access).

| Strategy | Mechanism | Strengths | Weaknesses |
|---|---|---|---|
| **A — Pessimistic** | `SELECT … FOR UPDATE` (DISTINCT ids, `ORDER BY account_id`) then UPDATE | simple to reason about; strong for multi-row invariants; deterministic lock order ⇒ no deadlock | holds row locks for the txn; lower throughput on hot rows |
| **B — Conditional update** | single guarded `UPDATE … WHERE allows_negative OR amount-@amt>=0 RETURNING` | **no read-modify-write window (no TOCTOU)**; minimal lock hold; highest throughput | "0 rows" is ambiguous (needs a preflight existence read); hard for complex multi-row rules |
| **C — Optimistic CAS** | read `amount,version` → `UPDATE … WHERE version=@expected` → retry | no locks held; great at low contention; `version` already exposed by the balance API | wasted work/retries under high contention; needs a retry budget |
| Naive | read → compute → plain UPDATE | — | **unsafe**: lost updates, overspend, audit breaks. Kept only to demonstrate the bug. |

## Decision

**Default = Strategy B (atomic conditional update).** The core invariant ("don't go below zero unless
`allows_negative`") is naturally a single-row predicate, which maps exactly to one guarded `UPDATE` that
eliminates the read-modify-write race and gives the highest throughput on hot accounts — the behavior large
ledger systems favor for the hot debit path. Pessimistic (A) remains the right tool when one transaction must
hold several balances under a richer invariant; optimistic (C) is provided for low-contention/read-heavy
profiles and to exercise the `version` token.

**Strategy selection is a deployment/configuration decision, not a client concern** (`Ledger:ConcurrencyStrategy`).
Clients cannot choose it. An `X-Concurrency-Strategy` request header exists purely as a **test hook**, honored
only when `Ledger:AllowStrategyOverride=true` — enabled in the dev `docker-compose` stack so the K6 suite can
compare all strategies in one run, and **off in production**.

## Production hardening

- **One atomic transaction per movement**: balance mutation (Dapper) + immutable double-entry insert (EF) +
  commit, all on the same `NpgsqlConnection/NpgsqlTransaction`. All-or-nothing.
- **Bounded retry with jittered backoff** (`LedgerService`) for transient contention — PostgreSQL
  serialization failures (`40001`) and deadlocks (`40P01`) — and optimistic-CAS exhaustion
  (`ConcurrencyConflictException`). Each attempt is atomic and rolls back fully, so retries cannot double-spend.
- **Isolation**: default `READ COMMITTED` is sufficient for A and B; C relies on the per-statement snapshot so
  a re-read after a lost CAS sees the latest committed version and converges.
- **Deadlock note**: A serializes lock acquisition by `account_id` order (no deadlocks). B and C can deadlock
  on cross-account contention (e.g., the ring test) and rely on the retry policy; this is an explicit trade-off.
- **Idempotency** (separate feature) complements this: it makes a *client* retry of a whole request safe,
  which the internal contention-retry here does not address.

## Consequences

- Correctness verified by `k6/01-race-withdrawals.js` (exactly M succeed, never negative, audit holds) and
  `k6/03-atomicity-ring.js` (global conservation) for A/B/C; the `naive` runs document the failure.
- The domain balance methods (`Account.TryDebit`/`Credit`) remain the model's canonical behavior and are used
  by the `naive` mutator; the safe strategies move the hot-path mutation to explicit SQL — a deliberate
  hybrid trade-off documented in `concurrency-strategies.md`.
