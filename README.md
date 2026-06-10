# Self-Auditing Transfer Ledger

A production-grade, double-entry money-transfer ledger built as an educational reference
(.NET 10 / C# 14, PostgreSQL, Vertical Slice + Minimal API). Built incrementally: Phase 1 is the
working skeleton; the hard interview problems (race conditions, request hashing, idempotency) are
added afterward, each as its own *problem → approach → implementation → verification* feature.

## Phase 1 — what's here

- **Schema** (`accounts`, `balances`, `transfers`, `ledger_entries`) with CHECK constraints, indexes,
  and an **append-only immutability trigger** (UPDATE/DELETE on the ledger raise an error).
- **Basic CRUD** money movement via a single, atomic `SaveChanges` per operation:
  - `POST /transfers` — between two accounts (validates `self_transfer`, `invalid_amount`,
    `currency_mismatch`, `insufficient_funds`).
  - `POST /deposits`, `POST /withdrawals` — against a per-currency **system account** (keeps
    double-entry balanced).
  - `POST /transfers/{txId}/reverse` — compensating reversal (`already_reversed` → 409).
  - `GET /accounts/{id}/balance`, `GET /accounts/{id}/entries` (keyset cursor), `GET /transfers/{txId}`.
- Errors are RFC 9457 **ProblemDetails** with a machine-readable `code`.

> Not yet implemented (later features): idempotency keys, SHA256 request hashing, race-condition
> hardening (the movement engine is intentionally a naive read-modify-write), Redis, K6 load tests.

## Run

```bash
docker compose up --build
```

The API applies EF Core migrations and seeds demo accounts on startup, then listens on
`http://localhost:8080`. PostgreSQL is exposed on `5432`.

Seeded accounts (fixed ids, see `requests.http`):

| Account | Id | Currency | Balance |
|---|---|---|---|
| Alice | `1111…1111` | USD | 1,000.00 |
| Bob | `2222…2222` | USD | 0.00 |
| Carol | `3333…3333` | EUR | 500.00 |

Amounts are **minor units** (integer cents) everywhere — never floats.

## Local development (without Docker)

Start a PostgreSQL on `localhost:5432` (db/user/pass `ledger`) and run:

```bash
dotnet run --project src/MoneyTransfer.Api
```

EF Core migrations live in `src/MoneyTransfer.Api/Infrastructure/Persistence/Migrations`.
