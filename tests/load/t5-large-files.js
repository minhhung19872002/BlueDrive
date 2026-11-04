// T5 - large media: UPLOADERS editors (default 10, perf034+) each send FILES_PER_VU files of MIN_MB - MAX_MB (200 MB - 2 GB)
// through tus in 50 MB chunks, exactly like the web client, while VIEWERS users (default 20, perf001+) play a PLAY_MB video
// (default 150 MB, uploaded and shared in setup) with Range requests: start, then random seeks. Measures upload throughput,
// the time until an upload is scanned (ClamAV marks files over CLAMAV_MAX_SCAN_MB "too large") and video start/seek latency.
import http from 'k6/http';
import encoding from 'k6/encoding';
import { sleep, fail } from 'k6';
import { Trend, Counter } from 'k6/metrics';
import exec from 'k6/execution';
import { BASE, login, userName, get, post, ok, summaryTo } from './lib.js';

const CHUNK = 50 * 1024 * 1024;
const MIN_MB = parseInt(__ENV.MIN_MB || '200', 10);
const MAX_MB = parseInt(__ENV.MAX_MB || '2048', 10);
const PLAY_MB = parseInt(__ENV.PLAY_MB || '150', 10);
const UPLOADERS = parseInt(__ENV.UPLOADERS || '10', 10);
const VIEWERS = parseInt(__ENV.VIEWERS || '20', 10);
const FIRST_EDITOR = 34; // PerfSeed: perf034 and above are editors (doc.upload)

const uploadMbps = new Trend('upload_throughput_mbit');
const uploadTotal = new Trend('upload_total_s');
const scanWait = new Trend('upload_until_scanned_s');
const videoStart = new Trend('video_first_chunk_ms', true);
const videoSeek = new Trend('video_seek_ms', true);
const uploadedBytes = new Counter('uploaded_bytes');

export const options = {
  setupTimeout: '15m',
  scenarios: {
    uploads: { executor: 'per-vu-iterations', exec: 'uploader', vus: UPLOADERS, iterations: parseInt(__ENV.FILES_PER_VU || '2', 10), maxDuration: '60m' },
    playback: { executor: 'constant-vus', exec: 'viewer', vus: VIEWERS, duration: __ENV.DURATION || '20m' },
  },
  thresholds: {
    video_first_chunk_ms: ['p(95)<2000'],
    video_seek_ms: ['p(95)<2000'],
    'http_req_failed{kind:upload}': ['rate<0.01'],
    checks: ['rate>0.99'],
  },
  summaryTrendStats: ['avg', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
};

function loginPaced(username) {
  let s = login(username);
  for (let retry = 0; s.status === 429 && retry < 10; retry++) { sleep(6); s = login(username); }
  if (s.status !== 200) fail(`Đăng nhập ${username} lỗi ${s.status}`);
  sleep(parseInt(__ENV.LOGIN_PACE_MS || '1100', 10) / 1000);
  return s;
}

let chunk = null;
function buffer() {
  if (chunk) return chunk;
  const seed = new Uint8Array(65536);
  for (let i = 0; i < seed.length; i++) seed[i] = (i * 2654435761 + exec.vu.idInTest) & 0xff;
  const big = new Uint8Array(CHUNK);
  for (let off = 0; off < CHUNK; off += seed.length) big.set(seed, off);
  // The server checks the signature of what it receives (FileTypeInspector): an .mp4 must start with an ISO-BMFF "ftyp" box.
  big.set([0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70, 0x69, 0x73, 0x6f, 0x6d, 0, 0, 2, 0, 0x69, 0x73, 0x6f, 0x6d, 0x69, 0x73, 0x6f, 0x32], 0);
  chunk = big.buffer;
  return chunk;
}

const tusMeta = (pairs) => Object.entries(pairs).map(([k, v]) => `${k} ${encoding.b64encode(v)}`).join(',');

/** Uploads `size` bytes through tus + /uploads/{id}/complete and returns the complete response (null on failure). */
function tusUpload(tokenOf, workspaceId, name, size) {
  const headers = (extra) => Object.assign({ Authorization: `Bearer ${tokenOf()}`, 'Tus-Resumable': '1.0.0' }, extra);
  const create = http.post(`${BASE}/tus`, null, {
    headers: headers({ 'Upload-Length': String(size), 'Upload-Metadata': tusMeta({ filename: name, filetype: 'video/mp4', workspaceId }) }),
    tags: { name: 'POST /tus', kind: 'upload' },
  });
  if (!ok(create, 'tus create 201')) return null;
  const location = create.headers.Location;
  const url = location.startsWith('http') ? location : `${BASE}${location}`;
  const data = buffer();
  for (let offset = 0; offset < size;) {
    const len = Math.min(CHUNK, size - offset);
    const r = http.patch(url, len === CHUNK ? data : data.slice(0, len), {
      headers: headers({ 'Upload-Offset': String(offset), 'Content-Type': 'application/offset+octet-stream' }),
      tags: { name: 'PATCH /tus/{id}', kind: 'upload' }, timeout: '180s',
    });
    if (!ok(r, 'tus patch 204')) return null;
    offset = parseInt(r.headers['Upload-Offset'], 10);
    uploadedBytes.add(len);
  }
  const done = http.post(`${BASE}/api/v1/uploads/${location.split('/').pop()}/complete`, JSON.stringify({ fileName: name, workspaceId }), {
    headers: { Authorization: `Bearer ${tokenOf()}`, 'Content-Type': 'application/json' }, tags: { name: 'POST /uploads/{id}/complete', kind: 'upload' }, timeout: '300s',
  });
  return ok(done, 'complete 200') ? done : null;
}

export function setup() {
  const uploaders = [];
  for (let i = 0; i < UPLOADERS; i++) uploaders.push(loginPaced(userName(FIRST_EDITOR + i)));
  const viewers = [];
  for (let i = 1; i <= VIEWERS; i++) viewers.push(loginPaced(userName(i)));

  const owner = uploaders[0];
  const done = tusUpload(() => owner.token, owner.personalWs, `Video phát thử ${Date.now()}.mp4`, PLAY_MB * 1024 * 1024);
  if (!done) fail('Không tải lên được video phát thử.');
  const videoId = done.json().fileId;
  const share = post(owner, '/api/v1/shares', {
    resourceType: 'file', resourceId: videoId, accessLevel: 'download', message: '[perf] T5',
    targets: viewers.map((v) => ({ type: 'user', id: v.userId })),
  }, 'POST /shares');
  if (!ok(share, 'share 200')) fail(`Không chia sẻ được video: ${share.status} ${share.body}`);
  return { uploaders, viewers, videoId };
}

// Tokens live 15 minutes and a 2 GB upload can take longer: each VU re-logs in when its token is about to expire.
let mine = null;
function me(list) {
  if (!mine) mine = Object.assign({}, list[(exec.vu.idInTest - 1) % list.length]);
  if (mine.expires - Date.now() < 120000) {
    const s = login(mine.username);
    if (s.status === 200) mine = s;
  }
  return mine;
}

export function uploader(data) {
  const s = me(data.uploaders);
  const size = (MIN_MB + Math.floor(Math.random() * (MAX_MB - MIN_MB + 1))) * 1024 * 1024;
  const started = Date.now();
  const done = tusUpload(() => me(data.uploaders).token, s.personalWs, `Video lớn ${s.username} ${Date.now()}.mp4`, size);
  if (!done) return;
  const seconds = (Date.now() - started) / 1000;
  uploadTotal.add(seconds);
  uploadMbps.add((size * 8) / 1e6 / seconds);

  // Wait until the virus scan has an outcome (clean, or "too large" over CLAMAV_MAX_SCAN_MB).
  const fileId = done.json().fileId;
  const t0 = Date.now();
  for (let i = 0; i < 120; i++) {
    const f = get(me(data.uploaders), `/api/v1/files/${fileId}`, 'GET /files/{id}', 'poll');
    const st = f.status === 200 ? f.json().scanStatus : null;
    if (st && st !== 'pending' && st !== 'scanning') { scanWait.add((Date.now() - t0) / 1000); break; }
    sleep(5);
  }
}

export function viewer(data) {
  const s = me(data.viewers);
  const u = get(s, `/api/v1/files/${data.videoId}/download-url?inline=true`, 'GET /files/{id}/download-url');
  if (!ok(u, 'download-url 200')) return sleep(2);
  const content = `${BASE}${u.json().url}`;
  const tags = { name: 'GET /content/{token} (video)', kind: 'read' };
  const first = http.get(content, { headers: { Range: 'bytes=0-1048575' }, tags });
  if (ok(first, 'video range 206')) videoStart.add(first.timings.duration);
  const total = parseInt(((first.headers['Content-Range'] || '').split('/')[1]) || '0', 10);
  for (let i = 0; i < 4 && total > 2097152; i++) {
    sleep(3 + Math.random() * 5); // watching, then a seek
    const from = Math.floor(Math.random() * (total - 1048576));
    const seek = http.get(content, { headers: { Range: `bytes=${from}-${from + 1048575}` }, tags });
    if (ok(seek, 'video seek 206')) videoSeek.add(seek.timings.duration);
  }
  sleep(2);
}

export const handleSummary = summaryTo(__ENV.RESULT_NAME || 't5-large-files');
