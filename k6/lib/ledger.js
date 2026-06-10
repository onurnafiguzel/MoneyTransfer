import http from 'k6/http';
import { check } from 'k6';

export const BASE = __ENV.BASE_URL || 'http://localhost:8080';

// Pin a concurrency strategy per request (honored only when the API has AllowStrategyOverride=true).
export function headers(strategy) {
  const h = { 'Content-Type': 'application/json' };
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
