import { createHash, randomBytes } from 'node:crypto'
import { expect, test, type Page } from '@playwright/test'

/**
 * ONLYOFFICE Desktop Editors provider "bluedrive" (Sync-Phase 8b). The desktop app is simulated by window.AscDesktopEditor, which records
 * the execCommand calls the pages make (portal:login, open:document…).
 */
async function fakeDesktopEditors(page: Page) {
  await page.addInitScript(() => {
    const calls: { command: string; params: unknown }[] = []
    ;(window as unknown as { __oo: typeof calls }).__oo = calls
    ;(window as unknown as { AscDesktopEditor: unknown }).AscDesktopEditor = {
      execCommand: (command: string, params: string) => calls.push({ command, params: params ? JSON.parse(params) : null }),
    }
  })
}

const calls = (page: Page) => page.evaluate(() => (window as unknown as { __oo: { command: string; params: Record<string, string> }[] }).__oo)

/** A device session of BlueDrive Desktop (PKCE), like the real app gets it. */
async function deviceToken(page: Page, request: import('@playwright/test').APIRequestContext) {
  const port = 50000 + Math.floor(Math.random() * 9000)
  const redirect = `http://127.0.0.1:${port}/callback`
  const verifier = randomBytes(48).toString('base64url')
  const challenge = createHash('sha256').update(verifier).digest('base64url')
  let callback = ''
  await page.route(`http://127.0.0.1:${port}/**`, async (route) => {
    callback = route.request().url()
    await route.fulfill({ status: 200, contentType: 'text/html', body: '<p>OK</p>' })
  })
  await page.goto(`/connect/authorize?client_id=bluedrive-desktop&redirect_uri=${encodeURIComponent(redirect)}&code_challenge=${challenge}` +
    '&code_challenge_method=S256&state=oo&device_name=PC-OO')
  await page.getByRole('button', { name: 'Cho phép' }).click()
  await expect.poll(() => callback, { timeout: 15_000 }).toContain('code=')
  const token = await request.post('/api/v1/connect/token', {
    form: {
      grant_type: 'authorization_code', code: new URL(callback).searchParams.get('code')!, code_verifier: verifier, redirect_uri: redirect,
      client_id: 'bluedrive-desktop', device_name: 'PC-OO', device_os: 'Windows 11', app_version: '1.0.0', machine_id: `oo-${Date.now()}`,
    },
  })
  expect(token.ok(), await token.text()).toBeTruthy()
  return (await token.json()).access_token as string
}

test('ONLYOFFICE Desktop Editors: vào BlueDrive, mở tài liệu bằng vé từ BlueDrive Desktop', async ({ page, request, browser }) => {
  const token = await deviceToken(page, request)
  const auth = { Authorization: `Bearer ${token}` }
  const me = await (await request.get('/api/v1/auth/me', { headers: auth })).json()
  const upload = await request.post('/api/v1/files/upload', {
    headers: auth,
    multipart: {
      file: { name: `Cùng soạn ${Date.now()}.docx`, mimeType: 'application/octet-stream', buffer: Buffer.from('PK') },
      workspaceId: me.personalWorkspaceId,
    },
  })
  // A minimal .docx may be refused by the content check: fall back to any existing Word file of the user.
  let fileId: string
  if (upload.ok()) fileId = (await upload.json()).fileId
  else {
    const items = await (await request.get(`/api/v1/workspaces/${me.personalWorkspaceId}/items?pageSize=200`, { headers: auth })).json()
    const docx = items.items.find((i: { extension: string }) => i.extension === 'docx')
    test.skip(!docx, 'Không có tệp Word để thử')
    fileId = docx.id
  }

  // "Cùng soạn thảo bằng ONLYOFFICE": the desktop app gets a one-time link…
  const link = await (await request.post('/api/v1/onlyoffice-desktop/tickets', { headers: auth, data: { fileId } })).json()
  const openPath = new URL(link.url, 'http://x').pathname + new URL(link.url, 'http://x').search

  // …which ONLYOFFICE opens in a fresh browser profile (not signed in to BlueDrive).
  const context = await browser.newContext({ storageState: { cookies: [], origins: [] } })
  const oo = await context.newPage()
  await fakeDesktopEditors(oo)
  await oo.goto(openPath)
  await expect(oo).toHaveURL(new RegExp(`/files/${fileId}/edit`), { timeout: 15_000 })
  const login = (await calls(oo)).find((c) => c.command === 'portal:login')
  expect(login?.params.provider).toBe('bluedrive')
  expect(login?.params.userId).toBe(me.id)
  expect(await oo.evaluate(() => document.documentElement.classList.contains('bd-oo-desktop'))).toBe(true)

  // The link works once.
  await oo.goto(openPath)
  await expect(oo.getByText('Không mở được tài liệu')).toBeVisible()
  await context.close()
})

test('ONLYOFFICE Desktop Editors: trang kết nối đăng ký BlueDrive vào "Đám mây đã kết nối"', async ({ page }) => {
  await fakeDesktopEditors(page)
  await page.goto('/oo-desktop')
  await expect(page).toHaveURL(/\/my-drive/)
  expect((await calls(page)).map((c) => c.command)).toContain('portal:login')
  await expect(page.locator('.bd-header')).toBeHidden()
})
