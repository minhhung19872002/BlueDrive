// T2 - stress: the T1 office mix, ramping 0 -> 100 -> 200 -> 300 -> 400 VUs (3 minutes per step, then 5 minutes at 400)
// to find where p95 crosses 2 s. VUs above PERF_USERS reuse the perf accounts (one more browser tab per person).
// The HSMT thresholds are recorded but do not abort the run: the point is to see the breaking point, not to pass.
import mixed, { setup as mixedSetup } from './t1-mixed.js';
import { HSMT_THRESHOLDS, summaryTo } from './lib.js';

const PEAK = parseInt(__ENV.PEAK || '400', 10);
const STEP = __ENV.STEP || '3m';
const stages = [];
for (let v = 100; v <= PEAK; v += 100) stages.push({ duration: '1m', target: v }, { duration: STEP, target: v });
stages.push({ duration: __ENV.HOLD || '5m', target: PEAK }, { duration: '1m', target: 0 });

export const options = {
  setupTimeout: '10m',
  scenarios: { ramp: { executor: 'ramping-vus', startVUs: 0, stages, gracefulRampDown: '30s' } },
  thresholds: HSMT_THRESHOLDS,
  summaryTrendStats: ['avg', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
};

export function setup() {
  return mixedSetup();
}

export default function (data) {
  mixed(data);
}

export const handleSummary = summaryTo(__ENV.RESULT_NAME || `t2-ramp-${PEAK}`);
