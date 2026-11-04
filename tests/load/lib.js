// Shared helpers for the BlueDrive load tests (tests/load/t*.js, docs/bao-cao-kiem-thu-tai.md).
// Environment: BASE_URL (default http://localhost:8091 = perf stack through the shaped "edge" link), PERF_USERS (100),
// PERF_USER_PASSWORD (deploy/.env.perf), LOGIN_PACE_MS (pause between the logins done in setup).
import http from 'k6/http';
import { check, sleep, fail } from 'k6';
import exec from 'k6/execution';

export const BASE = (__ENV.BASE_URL || 'http://localhost:8091').replace(/\/$/, '');
export const USERS = parseInt(__ENV.PERF_USERS || '100', 10);
const PASSWORD = __ENV.PERF_USER_PASSWORD;
const JSON_HEADERS = { 'Content-Type': 'application/json' };

export function userName(i) {
  return 'perf' + String(i).padStart(3, '0');
}

export function login(username, password, tags) {
  const res = http.post(`${BASE}/api/v1/auth/login`, JSON.stringify({ username, password: password || PASSWORD }), {
    headers: JSON_HEADERS, tags: Object.assign({ name: 'POST /auth/login', kind: 'auth' }, tags || {}),
  });
  if (res.status !== 200) return { status: res.status, body: res.body };
  const b = res.json();
  return {
    status: 200, username, token: b.accessToken, refresh: b.refreshToken, expires: Date.parse(b.accessTokenExpiresAt),
    userId: b.user.id, personalWs: b.user.personalWorkspaceId,
  };
}

/** Logs every perf user in once (setup), paced below the per-IP login limit unless LOGIN_PACE_MS says otherwise. */
export function loginAll(count) {
  if (!PASSWORD) fail('Thiếu PERF_USER_PASSWORD (lấy từ deploy/.env.perf).');
  const pace = parseInt(__ENV.LOGIN_PACE_MS || '1100', 10);
  const sessions = [];
  for (let i = 1; i <= (count || USERS); i++) {
    let s = login(userName(i));
    for (let retry = 0; s.status === 429 && retry < 10; retry++) { sleep(6); s = login(userName(i)); }
    if (s.status !== 200) fail(`Đăng nhập ${userName(i)} lỗi ${s.status}: ${s.body}`);
    sessions.push(s);
    if (pace > 0) sleep(pace / 1000);
  }
  return sessions;
}

// Per-VU session: VU n uses user n (mod count). Only the VU that "owns" a setup session refreshes it, so refresh-token
// rotation never invalidates a session another VU is using; the extra VUs of a ramp (T2) log in on their own before the
// shared token expires and from then on own that new session.
let mine = null;
export function session(data) {
  const vu = exec.vu.idInTest - 1;
  if (!mine) mine = Object.assign({}, data.sessions[vu % data.sessions.length], { owner: vu < data.sessions.length });
  if (!mine.owner && mine.expires - Date.now() < 120000) {
    const s = login(mine.username);
    if (check(s, { 'relogin 200': (x) => x.status === 200 })) mine = Object.assign(s, { owner: true });
  } else if (mine.owner && mine.expires - Date.now() < 120000) {
    const r = http.post(`${BASE}/api/v1/auth/refresh`, JSON.stringify({ refreshToken: mine.refresh }), {
      headers: JSON_HEADERS, tags: { name: 'POST /auth/refresh', kind: 'auth' },
    });
    if (check(r, { 'refresh 200': (x) => x.status === 200 })) {
      const b = r.json();
      mine.token = b.accessToken; mine.refresh = b.refreshToken; mine.expires = Date.parse(b.accessTokenExpiresAt);
    }
  }
  return mine;
}

export function params(s, name, kind, extra) {
  return Object.assign({ headers: { Authorization: `Bearer ${s.token}`, 'Content-Type': 'application/json' }, tags: { name, kind } }, extra || {});
}

export function get(s, path, name, kind) {
  return http.get(`${BASE}${path}`, params(s, name, kind || 'read'));
}

export function post(s, path, body, name, kind) {
  return http.post(`${BASE}${path}`, body === null ? null : JSON.stringify(body), params(s, name, kind || 'write'));
}

export function put(s, path, body, name, kind) {
  return http.put(`${BASE}${path}`, JSON.stringify(body), params(s, name, kind || 'write'));
}

export function ok(res, label) {
  return check(res, { [label]: (r) => r.status >= 200 && r.status < 300 });
}

export function pick(arr) {
  return arr[Math.floor(Math.random() * arr.length)];
}

export const SEARCH_TERMS = ['dan so', 'già hóa', 'ke hoach truyen thong', 'áp phích', 'video phong su', 'bao cao tong ket', 'Nghệ An', 'tap huan',
  'infographic', 'sàng lọc sơ sinh', 'tu van tien hon nhan', 'chat luong dan so', 'Cà Mau', 'bản tin'];

/** Thresholds of the HSMT non-functional requirements: p95 < 2 s, no request over 10 s, < 1 % errors. */
export const HSMT_THRESHOLDS = {
  'http_req_duration{kind:read}': ['p(95)<2000', 'max<10000'],
  'http_req_duration{kind:write}': ['p(95)<2000', 'max<10000'],
  http_req_failed: ['rate<0.01'],
  checks: ['rate>0.99'],
};

export function summaryTo(name) {
  return function (data) {
    const out = {};
    out[`tests/load/results/${name}.json`] = JSON.stringify(data, null, 1);
    out.stdout = textSummary(data);
    return out;
  };
}

function textSummary(data) {
  const m = data.metrics;
  const line = (k) => {
    const v = m[k] && m[k].values;
    if (!v) return '';
    switch (m[k].type) {
      case 'trend': {
        const u = m[k].contains === 'time' ? ' ms' : '';
        const f = (x) => (m[k].contains === 'time' ? (x || 0).toFixed(0) : (x || 0).toFixed(1));
        return `${k}: avg ${f(v.avg)}${u}, med ${f(v.med)}${u}, p95 ${f(v['p(95)'])}${u}, p99 ${f(v['p(99)'])}${u}, max ${f(v.max)}${u}\n`;
      }
      case 'rate': return `${k}: ${(v.rate * 100).toFixed(2)} %\n`;
      case 'counter': return `${k}: ${v.count} (${v.rate.toFixed(2)}/s)\n`;
      case 'gauge': return `${k}: ${v.value} (min ${v.min}, max ${v.max})\n`;
      default: return '';
    }
  };
  let s = '\n';
  for (const k of Object.keys(m).sort()) s += line(k);
  const failed = Object.entries(data.metrics).filter(([, v]) => v.thresholds && Object.values(v.thresholds).some((t) => !t.ok)).map(([k]) => k);
  s += failed.length ? `\nKHÔNG ĐẠT ngưỡng: ${failed.join(', ')}\n` : '\nĐạt tất cả ngưỡng.\n';
  return s;
}
