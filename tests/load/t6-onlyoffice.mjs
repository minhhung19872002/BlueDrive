// T6 - ONLYOFFICE co-editing: SESSIONS (default 25) editors each open their own new Word document in a real Chromium page and keep
// it open for HOLD_S seconds. ONLYOFFICE Docs Community Edition accepts 20 simultaneous editor connections; the run records
// for each session the time until the editor iframe has loaded and whether the Document Server raised a warning or error
// (the web app shows them as a notification), plus a screenshot. Run with node from the repository root:
//   node tests/load/t6-onlyoffice.mjs   (BASE_URL, PERF_USER_PASSWORD, SESSIONS, HOLD_S; Chromium from web/node_modules)
import { createRequire } from 'node:module';
import { mkdirSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const { chromium } = createRequire(path.join(here, '../../web/package.json'))('@playwright/test');

const BASE = (process.env.BASE_URL || 'http://localhost:8091').replace(/\/$/, '');
const PASSWORD = process.env.PERF_USER_PASSWORD;
const SESSIONS = parseInt(process.env.SESSIONS || '25', 10);
const HOLD_S = parseInt(process.env.HOLD_S || '120', 10);
const FIRST_EDITOR = 34;
const outDir = path.join(here, 'results', 't6-onlyoffice');
if (!PASSWORD) throw new Error('Thiếu PERF_USER_PASSWORD (lấy từ deploy/.env.perf).');
mkdirSync(outDir, { recursive: true });

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
async function api(method, url, token, body) {
  const r = await fetch(`${BASE}${url}`, {
    method, headers: { 'Content-Type': 'application/json', ...(token ? { Authorization: `Bearer ${token}` } : {}) },
    body: body ? JSON.stringify(body) : undefined,
  });
  if (!r.ok) throw new Error(`${method} ${url}: ${r.status} ${await r.text()}`);
  return r.status === 204 ? null : r.json();
}

const browser = await chromium.launch();
const results = [];
const pages = [];
try {
  for (let i = 0; i < SESSIONS; i++) {
    const username = `perf${String(FIRST_EDITOR + i).padStart(3, '0')}`;
    const login = await api('POST', '/api/v1/auth/login', null, { username, password: PASSWORD });
    const doc = await api('POST', '/api/v1/office/new', login.accessToken, { workspaceId: login.user.personalWorkspaceId, kind: 'docx', name: `Soạn thảo đồng thời ${i + 1}` });
    const context = await browser.newContext({ locale: 'vi-VN', viewport: { width: 1366, height: 800 } });
    const state = JSON.stringify({ state: { accessToken: login.accessToken, refreshToken: login.refreshToken, user: login.user }, version: 0 });
    await context.addInitScript((s) => localStorage.setItem('bluedrive-auth', s), state);
    const page = await context.newPage();
    const started = Date.now();
    await page.goto(`${BASE}/files/${doc.fileId}/edit`);
    const result = { session: i + 1, username, fileId: doc.fileId, loadMs: null, notice: null, editorText: null };
    try {
      const frame = page.frameLocator('iframe[name="frameEditor"]');
      // The editor is usable once its toolbar is rendered inside the Document Server iframe.
      await frame.locator('#toolbar, .toolbar').first().waitFor({ state: 'attached', timeout: 90_000 });
      result.loadMs = Date.now() - started;
    } catch (e) {
      result.notice = `Không mở được trình soạn thảo trong 90 giây: ${e.message.split('\n')[0]}`;
    }
    await sleep(5000); // limit warnings arrive just after the document opens
    const notice = page.locator('.ant-notification-notice');
    if (await notice.count()) result.notice = (await notice.first().innerText()).replace(/\s+/g, ' ');
    const editorFrame = page.frames().find((f) => f.name() === 'frameEditor');
    if (editorFrame) {
      const text = await editorFrame.locator('body').innerText().catch(() => '');
      const hit = text.split('\n').find((l) => /connection|kết nối|giới hạn|limit|chỉ xem|view only/i.test(l));
      result.editorText = hit ? hit.trim() : null;
    }
    await page.screenshot({ path: path.join(outDir, `phien-${String(i + 1).padStart(2, '0')}.png`) });
    results.push(result);
    pages.push(page);
    console.log(`${result.session}. ${username}: ${result.loadMs ?? '-'} ms ${result.notice ?? ''} ${result.editorText ?? ''}`);
  }
  console.log(`Giữ ${SESSIONS} phiên mở ${HOLD_S} giây...`);
  await sleep(HOLD_S * 1000);
} finally {
  await browser.close();
  writeFileSync(path.join(outDir, 'ket-qua.json'), JSON.stringify(results, null, 1));
}
const opened = results.filter((r) => r.loadMs !== null && !r.notice && !r.editorText);
const loads = results.filter((r) => r.loadMs !== null).map((r) => r.loadMs).sort((a, b) => a - b);
console.log(`\nMở được bình thường: ${opened.length}/${SESSIONS}; thời gian mở trung vị ${loads[Math.floor(loads.length / 2)] ?? '-'} ms, lớn nhất ${loads.at(-1) ?? '-'} ms.`);
for (const r of results.filter((x) => x.notice || x.editorText)) console.log(`  Phiên ${r.session}: ${r.notice ?? ''} ${r.editorText ?? ''}`);
