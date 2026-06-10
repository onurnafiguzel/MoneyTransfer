// Idempotency (Step A — DB-backed dedup + request-hash collision guard).
//
// A client retry, a double-click, or a Postman replay must NOT move money twice. Every write carries an
// Idempotency-Key; the server applies the movement at most once per key and returns the original result on
// replay. The request hash is the collision guard: same key + different payload → rejected (422).
//
// This test fires a burst of identical requests that all share ONE key, then asserts:
//   - money moved EXACTLY once (A/B balances), self-audit holds;
//   - each response is 201 (the created or replayed transfer) or 409 (a concurrent duplicate in flight) —
//     never a second distinct success;
//   - a later replay returns 201 without moving money again;
//   - same key + a changed amount → 422 idempotency_key_reuse.
//
// Run:
//   docker compose run --rm k6 run /scripts/02-idempotency.js
//   docker compose run --rm -e STRATEGY=pessimistic k6 run /scripts/02-idempotency.js

import http from 'k6/http';
import { check } from 'k6';
import { BASE, headers, createAccount, deposit, balanceOf, sumEntries } from './lib/ledger.js';

const STRATEGY = __ENV.STRATEGY || 'conditional';
const VUS = parseInt(__ENV.VUS || '12', 10);
const ITER = parseInt(__ENV.ITER || '1', 10);
const FUND = 100000; // A starts with 1,000.00
const AMOUNT = 100;  // single transfer A -> B

export const options = {
  scenarios: {
    burst: { executor: 'per-vu-iterations', vus: VUS, iterations: ITER, maxDuration: '60s' },
  },
  thresholds: { checks: ['rate==1.0'] },
};

export function setup() {
  const a = createAccount('USD', false, STRATEGY);
  const b = createAccount('USD', false, STRATEGY);
  deposit(a, FUND, STRATEGY);
  const key = `idem-burst-${Date.now()}-${Math.random().toString(16).slice(2)}`;
  console.log(`[idem] A=${a} B=${b}; ${VUS}x${ITER} identical requests share key=${key}, amount=${AMOUNT}`);
  return { a, b, key };
}

export default function (data) {
  // Every VU sends the SAME idempotency key + identical payload — a textbook retry storm.
  const res = http.post(
    `${BASE}/transfers`,
    JSON.stringify({ fromAccount: data.a, toAccount: data.b, amount: AMOUNT, reason: 'idem' }),
    { headers: headers(STRATEGY, data.key) },
  );
  // 201 = created or idempotent replay (same txId); 409 = a concurrent duplicate still committing.
  check(res, {
    '[idem] burst response is 201 or 409': (r) => r.status === 201 || r.status === 409,
  });
}

export function teardown(data) {
  const balA = balanceOf(data.a);
  const balB = balanceOf(data.b);
  const auditHolds = balA === sumEntries(data.a) && balB === sumEntries(data.b);
  console.log(`[idem] after burst: A=${balA} (expected ${FUND - AMOUNT}) B=${balB} (expected ${AMOUNT})`);

  // Replay: same key + same payload now returns the stored transfer; must NOT move money again.
  const replay = http.post(
    `${BASE}/transfers`,
    JSON.stringify({ fromAccount: data.a, toAccount: data.b, amount: AMOUNT, reason: 'idem' }),
    { headers: headers(STRATEGY, data.key) },
  );
  const balAafterReplay = balanceOf(data.a);

  // Reuse: same key + a DIFFERENT amount → rejected as key reuse (collision guard).
  const reuse = http.post(
    `${BASE}/transfers`,
    JSON.stringify({ fromAccount: data.a, toAccount: data.b, amount: AMOUNT + 1, reason: 'idem' }),
    { headers: headers(STRATEGY, data.key) },
  );

  check(null, {
    '[idem] money moved exactly once (A balance)': () => balA === FUND - AMOUNT,
    '[idem] money moved exactly once (B balance)': () => balB === AMOUNT,
    '[idem] self-audit holds A/B': () => auditHolds,
    '[idem] replay returns 201 (idempotent)': () => replay.status === 201,
    '[idem] replay did not move money again': () => balAafterReplay === FUND - AMOUNT,
    '[idem] same key + different payload → 422': () => reuse.status === 422,
  });
}
