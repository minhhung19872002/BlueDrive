// T9 - BlueDrive Desktop at scale: CLIENTS sync clients (default 50, editors perf034+) each keep a SignalR connection to /hubs/sync
// (as the desktop agent does), poll /sync/changes as a fallback, and every ~CHANGE_EVERY_S seconds the same user changes one of
// their files from "another device" (rename through the web API). Measured: time from the change to the changes_available
// event, and to the change being visible in /sync/changes. Spec target: a change reaches the other machines within 5 s.
import http from 'k6/http';
import ws from 'k6/ws';
import { check, fail, sleep } from 'k6';
import { Trend, Counter } from 'k6/metrics';
import exec from 'k6/execution';
import { BASE, login, userName, get, put, ok, summaryTo } from './lib.js';

const CLIENTS = parseInt(__ENV.CLIENTS || '50', 10);
const FIRST_EDITOR = 34;
const DURATION_S = parseInt(__ENV.DURATION_S || '600', 10);
const CHANGE_EVERY_S = parseInt(__ENV.CHANGE_EVERY_S || '30', 10);
const RS = '\u001e';

const notifyLatency = new Trend('sync_notify_ms', true);
const visibleLatency = new Trend('sync_visible_ms', true);
const missed = new Counter('sync_notifications_missed');

export const options = {
  setupTimeout: '10m',
  scenarios: { desktop: { executor: 'per-vu-iterations', vus: CLIENTS, iterations: 1, maxDuration: `${DURATION_S + 120}s` } },
  thresholds: {
    sync_notify_ms: ['p(95)<5000'],
    sync_visible_ms: ['p(95)<5000'],
    sync_notifications_missed: ['count==0'],
    'http_req_duration{kind:read}': ['p(95)<2000'],
    checks: ['rate>0.99'],
  },
  summaryTrendStats: ['avg', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
};

export function setup() {
  const sessions = [];
  for (let i = 0; i < CLIENTS; i++) {
    let s = login(userName(FIRST_EDITOR + i));
    for (let retry = 0; s.status === 429 && retry < 10; retry++) { sleep(6); s = login(userName(FIRST_EDITOR + i)); }
    if (s.status !== 200) fail(`Đăng nhập ${userName(FIRST_EDITOR + i)} lỗi ${s.status}`);
    sessions.push(s);
    sleep(parseInt(__ENV.LOGIN_PACE_MS || '1100', 10) / 1000);
  }
  return { sessions };
}

let mine = null;
function me(data) {
  if (!mine) mine = Object.assign({}, data.sessions[exec.vu.idInTest - 1]);
  if (mine.expires - Date.now() < 120000) {
    const s = login(mine.username);
    if (s.status === 200) mine = s;
  }
  return mine;
}

export default function (data) {
  const s = me(data);
  // A file of this user to change from "another device" (uploaded once per client).
  const up = http.post(`${BASE}/api/v1/files/upload`, {
    workspaceId: s.personalWs, file: http.file(`Đồng bộ ${s.username}`, `Đồng bộ ${s.username} ${Date.now()}.txt`, 'text/plain'),
  }, { headers: { Authorization: `Bearer ${s.token}` }, tags: { name: 'POST /files/upload', kind: 'write' } });
  if (!ok(up, 'upload 200')) return;
  const fileId = up.json().fileId;

  // Start like the agent: roots, snapshot of the personal root (gives the cursor), then the hub.
  const roots = get(s, '/api/v1/sync/roots', 'GET /sync/roots');
  if (!ok(roots, 'roots 200')) return;
  const root = roots.json().find((r) => r.workspaceId === s.personalWs);
  let cursor = 0;
  let page = null;
  do {
    const snap = get(s, `/api/v1/sync/snapshot?root_id=${encodeURIComponent(root.rootId)}${page ? `&page_token=${encodeURIComponent(page)}` : ''}`, 'GET /sync/snapshot');
    if (!ok(snap, 'snapshot 200')) return;
    cursor = snap.json().cursor;
    page = snap.json().nextPageToken;
  } while (page);

  const neg = http.post(`${BASE}/hubs/sync/negotiate?negotiateVersion=1`, null, { headers: { Authorization: `Bearer ${s.token}` }, tags: { name: 'POST /hubs/sync/negotiate', kind: 'read' } });
  if (!ok(neg, 'negotiate 200')) return;
  const wsUrl = `${BASE.replace(/^http/, 'ws')}/hubs/sync?id=${encodeURIComponent(neg.json().connectionToken)}&access_token=${encodeURIComponent(s.token)}`;

  let pending = null; // { at, name, notified }
  const pull = () => {
    const r = get(me(data), `/api/v1/sync/changes?cursor=${cursor}&limit=500`, 'GET /sync/changes');
    if (!ok(r, 'changes 200')) return false;
    const body = r.json();
    cursor = body.nextCursor;
    return pending !== null && body.changes.some((c) => c.itemId === fileId);
  };

  const res = ws.connect(wsUrl, { tags: { name: 'WS /hubs/sync' } }, (socket) => {
    socket.on('open', () => socket.send(JSON.stringify({ protocol: 'json', version: 1 }) + RS));
    socket.on('message', (raw) => {
      for (const frame of raw.split(RS)) {
        if (!frame) continue;
        const m = JSON.parse(frame);
        if (m.type !== 1 || m.target !== 'changes_available' || !pending) continue;
        if (!pending.notified) { pending.notified = true; notifyLatency.add(Date.now() - pending.at); }
        if (pull()) { visibleLatency.add(Date.now() - pending.at); pending = null; }
      }
    });
    socket.setInterval(() => socket.send(JSON.stringify({ type: 6 }) + RS), 15000); // SignalR keep-alive
    socket.setInterval(() => {
      if (pending) { missed.add(1); pending = null; } // previous change never arrived
      pending = { at: Date.now(), notified: false };
      ok(put(me(data), `/api/v1/files/${fileId}/name`, { name: `Đồng bộ ${s.username} ${Date.now()}.txt` }, 'PUT /files/{id}/name'), 'rename 2xx');
    }, (CHANGE_EVERY_S + Math.random() * 10) * 1000);
    socket.setTimeout(() => socket.close(), DURATION_S * 1000);
    socket.on('error', (e) => console.error(`ws ${s.username}: ${e.error()}`));
  });
  check(res, { 'ws 101': (r) => r && r.status === 101 });
}

export const handleSummary = summaryTo(__ENV.RESULT_NAME || 't9-sync');
