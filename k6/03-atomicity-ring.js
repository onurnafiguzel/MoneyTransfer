// Atomicity / conservation under concurrency: three accounts, many concurrent ring transfers
// (A->B, B->C, C->A). Money moves around but the TOTAL must never change, and each account's
// self-audit (balance == Σ entries) must hold.
//
//   Safe strategies: total conserved exactly; per-account audit holds.
//   Naive strategy: lost updates create/destroy money — INFORMATIONAL run (not gated).
//
// Run:
//   docker compose run --rm k6 run /scripts/03-atomicity-ring.js
//   docker compose run --rm -e STRATEGY=pessimistic k6 run /scripts/03-atomicity-ring.js
//   docker compose run --rm -e STRATEGY=naive       k6 run /scripts/03-atomicity-ring.js

import http from 'k6/http';
import { check } from 'k6';
import { BASE, headers, createAccount, deposit, balanceOf, sumEntries } from './lib/ledger.js';

const STRATEGY = __ENV.STRATEGY || 'conditional';
const VUS = parseInt(__ENV.VUS || '20', 10);
const ITER = parseInt(__ENV.ITER || '15', 10);
const FUND = 100000;  // each account's starting balance
const HOP = 100;      // amount moved per ring hop

export const options = {
  scenarios: {
    ring: { executor: 'per-vu-iterations', vus: VUS, iterations: ITER, maxDuration: '120s' },
  },
  thresholds: STRATEGY === 'naive' ? {} : { checks: ['rate==1.0'] },
};

export function setup() {
  const a = createAccount('USD', false, STRATEGY);
  const b = createAccount('USD', false, STRATEGY);
  const c = createAccount('USD', false, STRATEGY);
  deposit(a, FUND, STRATEGY);
  deposit(b, FUND, STRATEGY);
  deposit(c, FUND, STRATEGY);
  console.log(`[${STRATEGY}] ring A=${a} B=${b} C=${c}; each ${FUND}; ${VUS}x${ITER} concurrent hops of ${HOP}`);
  return { a, b, c, total: FUND * 3 };
}

export default function (data) {
  const ring = [[data.a, data.b], [data.b, data.c], [data.c, data.a]];
  const [from, to] = ring[(__VU + __ITER) % 3];
  http.post(
    `${BASE}/transfers`,
    JSON.stringify({ fromAccount: from, toAccount: to, amount: HOP, reason: 'ring' }),
    { headers: headers(STRATEGY) },
  );
}

export function teardown(data) {
  const balA = balanceOf(data.a);
  const balB = balanceOf(data.b);
  const balC = balanceOf(data.c);
  const total = balA + balB + balC;
  const auditHolds = balA === sumEntries(data.a) && balB === sumEntries(data.b) && balC === sumEntries(data.c);

  console.log(`[${STRATEGY}] balances A=${balA} B=${balB} C=${balC} total=${total} (expected ${data.total})`);

  const conserved = total === data.total;

  if (STRATEGY === 'naive') {
    console.log(`[naive] conservation broken? ${!conserved}; audit broken? ${!auditHolds}`);
    return; // informational only
  }

  check(null, {
    [`[${STRATEGY}] conservation (total unchanged)`]: () => conserved,
    [`[${STRATEGY}] self-audit holds for A/B/C`]: () => auditHolds,
  });
}
