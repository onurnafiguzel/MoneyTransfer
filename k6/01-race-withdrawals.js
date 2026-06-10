// Race condition: N concurrent withdrawals from ONE account whose balance only covers M (< N).
//
//   Safe strategies (pessimistic / conditional / optimistic): exactly M succeed, balance never goes
//   negative, and the self-audit holds (balance == Σ ledger entries).
//   Naive strategy: lost updates overspend and/or break the self-audit — this run is INFORMATIONAL
//   (not gated) and logs whether the bug reproduced.
//
// Run:
//   docker compose run --rm k6 run /scripts/01-race-withdrawals.js                    # default = conditional (B)
//   docker compose run --rm -e STRATEGY=pessimistic k6 run /scripts/01-race-withdrawals.js
//   docker compose run --rm -e STRATEGY=optimistic  k6 run /scripts/01-race-withdrawals.js
//   docker compose run --rm -e STRATEGY=naive       k6 run /scripts/01-race-withdrawals.js   # demonstrates the bug

import http from 'k6/http';
import { check } from 'k6';
import { BASE, headers, createAccount, deposit, balanceOf, sumEntries } from './lib/ledger.js';

const STRATEGY = __ENV.STRATEGY || 'conditional';
const N = parseInt(__ENV.N || '40', 10);   // concurrent withdrawals
const M = parseInt(__ENV.M || '25', 10);   // withdrawals the balance can cover
const UNIT = 100;                          // each withdrawal (minor units)
const FUNDED = UNIT * M;                   // exact starting balance => exactly M should succeed

export const options = {
  scenarios: {
    storm: { executor: 'per-vu-iterations', vus: N, iterations: 1, maxDuration: '60s' },
  },
  thresholds: STRATEGY === 'naive' ? {} : { checks: ['rate==1.0'] },
};

export function setup() {
  const acc = createAccount('USD', false, STRATEGY);
  deposit(acc, FUNDED, STRATEGY);
  console.log(`[${STRATEGY}] account ${acc} funded ${FUNDED}; firing ${N} concurrent withdrawals of ${UNIT} (safe M=${M})`);
  return { acc };
}

export default function (data) {
  http.post(
    `${BASE}/withdrawals`,
    JSON.stringify({ account: data.acc, amount: UNIT, reason: 'race' }),
    { headers: headers(STRATEGY) },
  );
}

export function teardown(data) {
  const apiBalance = balanceOf(data.acc);
  const ledgerSum = sumEntries(data.acc);
  const successful = (FUNDED - ledgerSum) / UNIT; // withdrawals reflected in the ledger

  console.log(`[${STRATEGY}] apiBalance=${apiBalance} ledgerSum=${ledgerSum} successfulWithdrawals=${successful} (safe max M=${M})`);

  const auditHolds = apiBalance === ledgerSum;
  const neverNegative = ledgerSum >= 0 && apiBalance >= 0;
  const noOverspend = successful <= M;

  if (STRATEGY === 'naive') {
    const bug = !(auditHolds && neverNegative && noOverspend);
    console.log(`[naive] race bug reproduced this run? ${bug} (audit=${auditHolds}, neg-safe=${neverNegative}, no-overspend=${noOverspend})`);
    return; // informational only
  }

  check(null, {
    [`[${STRATEGY}] self-audit holds (balance == sum entries)`]: () => auditHolds,
    [`[${STRATEGY}] never negative`]: () => neverNegative,
    [`[${STRATEGY}] no overspend (successful <= M)`]: () => noOverspend,
    [`[${STRATEGY}] exactly M succeeded`]: () => successful === M,
  });
}
