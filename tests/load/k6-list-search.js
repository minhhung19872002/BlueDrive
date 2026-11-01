// Load test: 50 concurrent users browsing folders and searching (spec §12 phase 11: p95 < 2 s).
// Run: docker run --rm -i --network host -e BASE_URL=http://localhost:8088 -e USERNAME=admin -e PASSWORD=... grafana/k6 run - < tests/load/k6-list-search.js
import http from 'k6/http';
import { check, sleep } from 'k6';

const BASE = __ENV.BASE_URL || 'http://localhost';

export const options = {
  scenarios: {
    browse: { executor: 'constant-vus', vus: 50, duration: __ENV.DURATION || '2m' },
  },
  thresholds: {
    'http_req_duration{kind:list}': ['p(95)<2000'],
    'http_req_duration{kind:search}': ['p(95)<2000'],
    'http_req_duration{kind:dashboard}': ['p(95)<2000'],
    http_req_failed: ['rate<0.01'],
  },
};

export function setup() {
  const res = http.post(`${BASE}/api/v1/auth/login`, JSON.stringify({ username: __ENV.USERNAME, password: __ENV.PASSWORD }), {
    headers: { 'Content-Type': 'application/json' },
  });
  check(res, { 'login 200': (r) => r.status === 200 });
  const body = res.json();
  return { token: body.accessToken, ws: body.user.personalWorkspaceId };
}

const terms = ['dan so', 'ke hoach', 'video', 'bao cao', 'anh', 'chien dich', 'truyen thong'];

export default function (data) {
  const headers = { Authorization: `Bearer ${data.token}`, 'Content-Type': 'application/json' };
  const list = http.get(`${BASE}/api/v1/workspaces/${data.ws}/items?page=1&pageSize=50&sort=-updatedAt`, { headers, tags: { kind: 'list' } });
  check(list, { 'list 200': (r) => r.status === 200 });

  const q = terms[Math.floor(Math.random() * terms.length)];
  const search = http.post(`${BASE}/api/v1/search`, JSON.stringify({ q, page: 1, pageSize: 20 }), { headers, tags: { kind: 'search' } });
  check(search, { 'search 200': (r) => r.status === 200 });

  const recent = http.get(`${BASE}/api/v1/recent?take=20`, { headers, tags: { kind: 'list' } });
  check(recent, { 'recent 200': (r) => r.status === 200 });

  const dash = http.get(`${BASE}/api/v1/dashboards/personal`, { headers, tags: { kind: 'dashboard' } });
  check(dash, { 'dashboard 200': (r) => r.status === 200 });
  sleep(1);
}
