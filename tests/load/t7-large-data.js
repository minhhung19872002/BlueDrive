// T7 - administration screens on a full database (PerfSeed: 200 000 files, 2 000 000 audit rows): audit-log search with the
// filters of the "Nhật ký" screen, storage statistics, the admin dashboard and alerts, by ADMINS concurrent administrators
// (perf001-perf003), plus the audit hash-chain verification (200 000 rows per call). Run it next to T1 (run.ps1 -Test t7 -WithT1)
// to see the cost on ordinary users too.
import { sleep, check, fail } from 'k6';
import { Trend } from 'k6/metrics';
import { login, userName, get, post, ok, pick, summaryTo } from './lib.js';
import exec from 'k6/execution';

const ADMINS = parseInt(__ENV.ADMINS || '3', 10);
const verifyTime = new Trend('audit_verify_ms', true);
const ACTIONS = ['login', 'upload', 'download', 'view', 'edit', 'share', 'delete'];

export const options = {
  scenarios: {
    admins: { executor: 'constant-vus', exec: 'admin', vus: ADMINS, duration: __ENV.DURATION || '10m' },
    verify: { executor: 'per-vu-iterations', exec: 'verify', vus: 1, iterations: parseInt(__ENV.VERIFY_RUNS || '3', 10), maxDuration: '30m' },
  },
  thresholds: {
    'http_req_duration{kind:read}': ['p(95)<2000', 'max<10000'],
    http_req_failed: ['rate<0.01'],
    checks: ['rate>0.99'],
  },
  summaryTrendStats: ['avg', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
};

export function setup() {
  const sessions = [];
  for (let i = 1; i <= Math.min(ADMINS, 3); i++) {
    const s = login(userName(i));
    if (s.status !== 200) fail(`Đăng nhập ${userName(i)} lỗi ${s.status}`);
    sessions.push(s);
  }
  return { sessions };
}

let mine = null;
function me(data) {
  if (!mine) mine = Object.assign({}, data.sessions[(exec.vu.idInTest - 1) % data.sessions.length]);
  if (mine.expires - Date.now() < 120000) {
    const s = login(mine.username);
    if (s.status === 200) mine = s;
  }
  return mine;
}

const isoDaysAgo = (d) => new Date(Date.now() - d * 86400000).toISOString();

export function admin(data) {
  const s = me(data);
  const k = Math.random();
  if (k < 0.5) {
    // Audit-log screen: first page, then a filter, then page 2 - the way an administrator investigates.
    const filters = [
      {},
      { actions: [pick(ACTIONS)] },
      { from: isoDaysAgo(7), to: isoDaysAgo(0) },
      { from: isoDaysAgo(90), to: isoDaysAgo(60), actions: [pick(ACTIONS)] },
      { q: pick(['bao cao', 'dân số', 'perf0', 'video']) },
      { userId: pick(data.sessions).userId },
      { ip: '172.' },
    ];
    const f = pick(filters);
    ok(post(s, '/api/v1/audit-logs/search?page=1&pageSize=50', f, 'POST /audit-logs/search', 'read'), 'audit search 200');
    sleep(2);
    ok(post(s, '/api/v1/audit-logs/search?page=2&pageSize=50', f, 'POST /audit-logs/search', 'read'), 'audit page 2 200');
  } else if (k < 0.75) {
    ok(get(s, `/api/v1/storage-stats?by=${pick(['user', 'organization', 'workspace'])}&page=1&pageSize=50&sort=${pick(['', '-percent', '-files'])}`, 'GET /storage-stats'), 'stats 200');
    ok(get(s, '/api/v1/storage-stats/totals', 'GET /storage-stats/totals'), 'totals 200');
  } else if (k < 0.9) {
    ok(get(s, '/api/v1/dashboards/admin', 'GET /dashboards/admin'), 'admin dashboard 200');
  } else {
    ok(post(s, '/api/v1/alerts/search?page=1&pageSize=50', {}, 'POST /alerts/search', 'read'), 'alerts 200');
    ok(get(s, '/api/v1/alerts/unacknowledged-count', 'GET /alerts/unacknowledged-count'), 'alert count 200');
  }
  sleep(3 + Math.random() * 5);
}

export function verify(data) {
  const s = me(data);
  const r = get(s, '/api/v1/audit-logs/verify', 'GET /audit-logs/verify', 'verify');
  if (ok(r, 'verify 200')) {
    verifyTime.add(r.timings.duration);
    check(r, { 'audit chain valid': (x) => x.json().valid === true });
  }
  sleep(30);
}

export const handleSummary = summaryTo(__ENV.RESULT_NAME || 't7-large-data');
