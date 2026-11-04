// T1 - mixed load of the target office: VUS users (default 100, one per perf user) for DURATION (default 15m), about 70 % reads
// and 30 % writes, each user with its own token and the rights of its role. HSMT criteria: p95 < 2 s, no request over 10 s, < 1 % errors.
// T4 (soak) is this script with DURATION=4h. Run: powershell -File tests/load/run.ps1 -Test t1
import http from 'k6/http';
import { sleep } from 'k6';
import exec from 'k6/execution';
import { BASE, USERS, loginAll, session, get, post, put, ok, pick, SEARCH_TERMS, HSMT_THRESHOLDS, summaryTo } from './lib.js';

const VUS = parseInt(__ENV.VUS || String(USERS), 10);
export const options = {
  setupTimeout: '10m',
  scenarios: { office: { executor: 'constant-vus', vus: VUS, duration: __ENV.DURATION || '15m' } },
  thresholds: HSMT_THRESHOLDS,
  summaryTrendStats: ['avg', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
};

export function setup() {
  return { sessions: loginAll(Math.min(VUS, USERS)) };
}

// Role by user number, as seeded by PerfSeed.cs: 1-3 admin, 4-13 approver, 14-33 viewer, others editor.
function role(username) {
  const n = parseInt(username.slice(4), 10);
  return n <= 3 ? 'admin' : n <= 13 ? 'approver' : n <= 33 ? 'viewer' : 'editor';
}

const payload = 'BlueDrive kiểm thử tải - nội dung tệp. '.repeat(30000); // ~1.2 MB of text, sliced per upload
let state = null;

function init(s) {
  const wsRes = get(s, '/api/v1/workspaces', 'GET /workspaces');
  ok(wsRes, 'workspaces 200');
  const all = wsRes.status === 200 ? wsRes.json() : [];
  const groups = all.filter((w) => w.type === 'group' || w.type === 'organization').map((w) => w.id);
  const root = get(s, `/api/v1/workspaces/${s.personalWs}/items?page=1&pageSize=100`, 'GET /workspaces/{id}/items');
  const myFolders = root.status === 200 ? root.json().items.filter((i) => i.type === 'folder').map((i) => i.id) : [];
  const myFiles = root.status === 200 ? root.json().items.filter((i) => i.type === 'file').map((i) => i.id) : [];
  state = { groups, myFolders, myFiles, seen: [], role: role(s.username) };
}

function browse(s) {
  const ws = state.groups.length ? pick(state.groups) : s.personalWs;
  const tree = get(s, `/api/v1/workspaces/${ws}/folder-tree`, 'GET /workspaces/{id}/folder-tree');
  ok(tree, 'folder-tree 200');
  let folder = null;
  if (tree.status === 200) {
    const flat = [];
    const walk = (nodes) => nodes.forEach((n) => { flat.push(n.id); walk(n.children || []); });
    walk(tree.json());
    if (flat.length) folder = pick(flat);
  }
  const list = get(s, `/api/v1/workspaces/${ws}/items?${folder ? `folderId=${folder}&` : ''}page=1&pageSize=50&sort=-updatedAt`, 'GET /workspaces/{id}/items');
  ok(list, 'items 200');
  if (list.status === 200) {
    const files = list.json().items.filter((i) => i.type === 'file').map((i) => i.id);
    state.seen = files.length ? files.slice(0, 20) : state.seen;
  }
}

function search(s) {
  const r = post(s, '/api/v1/search', { q: pick(SEARCH_TERMS), page: 1, pageSize: 20 }, 'POST /search', 'read');
  ok(r, 'search 200');
  if (r.status === 200 && r.json().items.length) state.seen = r.json().items.filter((h) => h.item.type === 'file').map((h) => h.item.id).slice(0, 20);
}

function viewFile(s) {
  if (!state.seen.length) return browse(s);
  const id = pick(state.seen);
  ok(get(s, `/api/v1/files/${id}?recordView=true`, 'GET /files/{id}'), 'file detail 200');
  ok(get(s, `/api/v1/files/${id}/comments`, 'GET /files/{id}/comments'), 'comments 200');
  const url = get(s, `/api/v1/files/${id}/download-url?inline=true`, 'GET /files/{id}/download-url');
  if (ok(url, 'download-url 200')) {
    const c = http.get(`${BASE}${url.json().url}`, { headers: { Range: 'bytes=0-1048575' }, tags: { name: 'GET /content/{token}', kind: 'read' } });
    ok(c, 'content 2xx');
  }
}

function home(s) {
  ok(get(s, '/api/v1/recent?take=20', 'GET /recent'), 'recent 200');
  ok(get(s, '/api/v1/dashboards/personal', 'GET /dashboards/personal'), 'dashboard 200');
  ok(get(s, '/api/v1/shares/with-me?page=1&pageSize=50', 'GET /shares/with-me'), 'with-me 200');
  ok(get(s, '/api/v1/notifications?page=1&pageSize=20', 'GET /notifications'), 'notifications 200');
}

function upload(s) {
  const size = 20000 + Math.floor(Math.random() * 1000000);
  const name = `Tải lên ${s.username} ${Date.now()} ${exec.scenario.iterationInTest}.txt`;
  const body = { workspaceId: s.personalWs, file: http.file(payload.slice(0, size), name, 'text/plain') };
  if (state.myFolders.length) body.folderId = pick(state.myFolders);
  const r = http.post(`${BASE}/api/v1/files/upload`, body, { headers: { Authorization: `Bearer ${s.token}` }, tags: { name: 'POST /files/upload', kind: 'write' } });
  if (ok(r, 'upload 200')) state.myFiles.push(r.json().fileId);
}

function editOwn(s) {
  if (state.myFiles.length < 3) return upload(s);
  const id = pick(state.myFiles);
  const op = Math.random();
  if (op < 0.3) {
    ok(put(s, `/api/v1/files/${id}/name`, { name: `Đổi tên ${s.username} ${Date.now()}.txt` }, 'PUT /files/{id}/name'), 'rename 2xx');
  } else if (op < 0.55) {
    ok(put(s, '/api/v1/files/metadata', { fileIds: [id], tags: [pick(['kiểm thử', 'dân số', 'báo cáo']), 'tải'] }, 'PUT /files/metadata'), 'metadata 2xx');
  } else if (op < 0.8 && state.myFolders.length) {
    ok(post(s, '/api/v1/items/move', { items: [{ type: 'file', id }], targetWorkspaceId: s.personalWs, targetFolderId: pick(state.myFolders) }, 'POST /items/move'), 'move 2xx');
  } else {
    ok(post(s, '/api/v1/items/delete', { items: [{ type: 'file', id }] }, 'POST /items/delete'), 'delete 2xx');
    state.myFiles = state.myFiles.filter((x) => x !== id);
  }
}

function social(s) {
  if (!state.seen.length) return browse(s);
  const id = pick(state.seen);
  if (Math.random() < 0.5) {
    ok(put(s, '/api/v1/favorites', { itemType: 'file', itemId: id, starred: Math.random() < 0.7 }, 'PUT /favorites'), 'favorite 2xx');
  } else {
    ok(post(s, `/api/v1/files/${id}/comments`, { content: `Ý kiến kiểm thử tải của ${s.username}` }, 'POST /files/{id}/comments'), 'comment 2xx');
  }
}

export default function (data) {
  const s = session(data);
  if (!state) init(s);
  const r = Math.random();
  if (r < 0.70) {
    // reads
    const k = Math.random();
    if (k < 0.35) browse(s); else if (k < 0.60) search(s); else if (k < 0.85) viewFile(s); else home(s);
  } else {
    // writes: viewers and approvers cannot upload (role rights), they star and comment
    const writer = state.role === 'editor' || state.role === 'admin';
    const k = Math.random();
    if (!writer || k < 0.25) social(s); else if (k < 0.6) upload(s); else editOwn(s);
  }
  sleep(1 + Math.random() * 3);
}

export const handleSummary = summaryTo(__ENV.RESULT_NAME || 't1-mixed');
