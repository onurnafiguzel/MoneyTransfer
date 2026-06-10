import http from 'k6/http';
import { check } from 'k6';

export const BASE = __ENV.BASE_URL || 'http://localhost:8080';

// Unique-enough idempotency key per call. Each write is a DISTINCT logical request unless a caller passes an
// explicit key (the idempotency test reuses ONE key on purpose to prove dedup).
let _keySeq = 0;
export function newKey() {
  // __VU/__ITER aren't defined in setup()/teardown() — guard so the helper works in every k6 lifecycle stage.
  const vu = typeof __VU !== 'undefined' ? __VU : 0;
  const it = typeof __ITER !== 'undefined' ? __ITER : 0;
  return `k6-${vu}-${it}-${Date.now()}-${_keySeq++}-${Math.random().toString(16).slice(2)}`;
}

// Headers for a write request. Always carries an Idempotency-Key (required by the API on POST money moves);
// pass `idemKey` to reuse a fixed key. `strategy` pins the concurrency strategy (honored only when the API
// has AllowStrategyOverride=true).
export function headers(strategy, idemKey) {
  const h = { 'Content-Type': 'application/json', 'Idempotency-Key': idemKey || newKey() };
  if (strategy) h['X-Concurrency-Strategy'] = strategy;
  return h;
}

export function createAccount(currency, allowsNegative, strategy) {
  const body = JSON.stringify({
    ownerRef: `k6-${__VU}-${Date.now()}-${Math.random()}`,
    currency,
    allowsNegative: !!allowsNegative,
  });
  const res = http.post(`${BASE}/accounts`, body, { headers: headers(strategy) });
  check(res, { 'account created (201)': (r) => r.status === 201 });
  return res.json('id');
}

export function deposit(accountId, amount, strategy) {
  const res = http.post(
    `${BASE}/deposits`,
    JSON.stringify({ account: accountId, amount, reason: 'k6 seed' }),
    { headers: headers(strategy) },
  );
  check(res, { 'deposit ok (201)': (r) => r.status === 201 });
  return res;
}

export function balanceOf(accountId) {
  return http.get(`${BASE}/accounts/${accountId}/balance`).json('balance');
}

// Sums every ledger entry for an account by walking the keyset cursor — the ground truth for the self-audit.
export function sumEntries(accountId) {
  let sum = 0;
  let cursor = null;
  do {
    const url = `${BASE}/accounts/${accountId}/entries?size=200` + (cursor ? `&cursor=${encodeURIComponent(cursor)}` : '');
    const body = http.get(url).json();
    for (const e of body.entries) sum += e.amount;
    cursor = body.nextCursor;
  } while (cursor);
  return sum;
}
