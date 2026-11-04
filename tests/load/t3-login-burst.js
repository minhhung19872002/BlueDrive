// T3 - start of the working day: LOGINS logins (default 200) within one minute, all from the same IP (an office behind NAT),
// cycling through the perf accounts. Every login must succeed (no 429) and stay under the HSMT response time.
import { Counter, Rate } from 'k6/metrics';
import exec from 'k6/execution';
import { USERS, login, userName, summaryTo } from './lib.js';

const LOGINS = parseInt(__ENV.LOGINS || '200', 10);
const throttled = new Counter('login_throttled_429');
const loginOk = new Rate('login_ok');

export const options = {
  scenarios: {
    morning: { executor: 'constant-arrival-rate', rate: LOGINS, timeUnit: '1m', duration: '1m', preAllocatedVUs: 50, maxVUs: 200 },
  },
  thresholds: {
    'http_req_duration{kind:auth}': ['p(95)<2000', 'max<10000'],
    login_ok: ['rate>0.99'],
    login_throttled_429: ['count==0'],
  },
  summaryTrendStats: ['avg', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
};

export default function () {
  const s = login(userName((exec.scenario.iterationInTest % USERS) + 1));
  if (s.status === 429) throttled.add(1);
  loginOk.add(s.status === 200);
}

export const handleSummary = summaryTo(__ENV.RESULT_NAME || 't3-login-burst');
