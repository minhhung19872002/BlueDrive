import { expect, test, type APIRequestContext, type Browser, type Page } from '@playwright/test'

/**
 * Two users share documents with each other through the UI (#19, #45, #49, #72):
 * A shares with B (edit) → B sees it in "Được chia sẻ", opens and renames it, gets a notification;
 * B shares back with A (view only) → A cannot rename it; A revokes → B loses access.
 * Users and files are created through the API and removed afterwards.
 */
const stamp = Date.now().toString().slice(-6)
const PW1 = 'Khoi@Tao12345'
const PW2 = 'Chia@Se2026x'
interface TestUser {
  id: string
  username: string
  fullName: string
  token: string
  ws: string
}
let api: APIRequestContext
let admin: Record<string, string>
const users: TestUser[] = []
const bearer = (u: TestUser) => ({ Authorization: `Bearer ${u.token}` })

async function createUser(username: string, fullName: string, roleId: string): Promise<TestUser> {
  const created = await api.post('/api/v1/users', {
    headers: admin,
    data: { username, fullName, isActive: true, password: PW1, roleIds: [roleId] },
  })
  expect(created.ok(), await created.text()).toBeTruthy()
  const { accessToken } = await (await api.post('/api/v1/auth/login', { data: { username, password: PW1 } })).json()
  // First login must change the password.
  const changed = await api.post('/api/v1/auth/change-password', {
    headers: { Authorization: `Bearer ${accessToken}` },
    data: { currentPassword: PW1, newPassword: PW2 },
  })
  expect(changed.ok(), await changed.text()).toBeTruthy()
  const token = (await changed.json()).accessToken
  const me = await (await api.get('/api/v1/auth/me', { headers: { Authorization: `Bearer ${token}` } })).json()
  return { id: me.id, username, fullName, token, ws: me.personalWorkspaceId }
}

async function upload(u: TestUser, name: string, text: string): Promise<string> {
  const r = await api.post('/api/v1/files/upload', {
    headers: bearer(u),
    multipart: { file: { name, mimeType: 'text/plain', buffer: Buffer.from(text) }, workspaceId: u.ws },
  })
  expect(r.ok(), await r.text()).toBeTruthy()
  return (await r.json()).fileId
}

async function loginPage(browser: Browser, u: TestUser): Promise<Page> {
  const ctx = await browser.newContext({ storageState: { cookies: [], origins: [] } })
  const page = await ctx.newPage()
  await page.goto('/login')
  await page.getByLabel(/tên đăng nhập/i).fill(u.username)
  await page.getByLabel(/mật khẩu/i).fill(PW2)
  await page.getByRole('button', { name: /đăng nhập/i }).click()
  await expect(page).toHaveURL(/\/$/, { timeout: 20_000 })
  return page
}

async function contextMenu(page: Page, name: string) {
  const row = page.getByRole('row', { name: new RegExp(name) })
  await expect(row).toBeVisible({ timeout: 15_000 })
  await expect(async () => {
    await page.keyboard.press('Escape')
    await row.getByText(name).click({ button: 'right' })
    await expect(page.getByRole('menuitem', { name: /Thông tin/ })).toBeVisible({ timeout: 2_000 })
  }).toPass({ timeout: 15_000 })
  await page.waitForTimeout(300)
}

async function shareWith(page: Page, fileName: string, user: TestUser, level: RegExp) {
  await contextMenu(page, fileName)
  await page.getByRole('menuitem', { name: /Chia sẻ$/ }).click()
  const dlg = page.getByRole('dialog')
  await dlg.getByRole('tab', { name: /Chia sẻ nội bộ/ }).click()
  await dlg.locator('.ant-select').first().click()
  await page.keyboard.type(user.fullName)
  await page.locator('.ant-select-dropdown:visible').getByText(user.fullName).first().click()
  await page.keyboard.press('Escape')
  await dlg.locator('.ant-select').nth(1).click()
  await page.locator('.ant-select-dropdown:visible').getByTitle(level).or(page.locator('.ant-select-dropdown:visible').getByText(level)).first().click()
  await dlg.getByRole('textbox', { name: /Lời nhắn/ }).fill(`Gửi ${user.fullName} (E2E)`)
  await Promise.all([
    page.waitForResponse((r) => r.url().endsWith('/api/v1/shares') && r.request().method() === 'POST' && r.ok()),
    dlg.getByRole('button', { name: /Chia sẻ$/ }).last().click(),
  ])
  await expect(dlg.getByText(user.fullName).last()).toBeVisible()
  await dlg.getByRole('button', { name: /Close|Đóng/ }).first().click()
}

test.beforeAll(async ({ playwright, baseURL }) => {
  api = await playwright.request.newContext({ baseURL })
  const login = await (await api.post('/api/v1/auth/login', {
    data: { username: process.env.E2E_USERNAME ?? 'admin', password: process.env.E2E_PASSWORD ?? 'Admin@Smoke2026' },
  })).json()
  admin = { Authorization: `Bearer ${login.accessToken}` }
  const roles: { id: string; code: string }[] = await (await api.get('/api/v1/roles', { headers: admin })).json()
  const editor = roles.find((r) => r.code === 'bien_tap')
  expect(editor, 'role bien_tap').toBeTruthy()
  users.push(await createUser(`e2esa${stamp}`, `Người Gửi ${stamp}`, editor!.id))
  users.push(await createUser(`e2esb${stamp}`, `Người Nhận ${stamp}`, editor!.id))
})

test.afterAll(async () => {
  for (const u of users) {
    const items = (await (await api.get(`/api/v1/workspaces/${u.ws}/items?pageSize=100`, { headers: bearer(u) })).json()).items ?? []
    const refs = items.map((i: { type: string; id: string }) => ({ type: i.type, id: i.id }))
    if (refs.length) {
      await api.post('/api/v1/items/delete', { headers: bearer(u), data: { items: refs } })
      await api.post('/api/v1/trash/purge', { headers: bearer(u), data: refs })
    }
    await api.delete(`/api/v1/users/${u.id}`, { headers: admin })
  }
  await api.dispose()
})

test('hai người dùng chia sẻ tài liệu qua lại', async ({ browser }) => {
  test.setTimeout(180_000)
  const [a, b] = users
  const fileA = `bao-cao-cua-A-${stamp}.txt`
  const fileB = `ke-hoach-cua-B-${stamp}.txt`
  const fileAId = await upload(a, fileA, 'Báo cáo của người gửi')
  const fileBId = await upload(b, fileB, 'Kế hoạch của người nhận')

  // A → B with edit rights.
  const pageA = await loginPage(browser, a)
  await pageA.goto('/my-drive')
  await shareWith(pageA, fileA, b, /^Chỉnh sửa$/)

  // B: notification + "Được chia sẻ" + open + rename (edit right).
  const pageB = await loginPage(browser, b)
  const notes = await (await api.get('/api/v1/notifications?page=1&pageSize=20', { headers: bearer(b) })).json()
  expect(JSON.stringify(notes.items)).toContain(fileA)
  await pageB.goto('/shared')
  const rowB = pageB.getByRole('row', { name: new RegExp(fileA) })
  await expect(rowB).toBeVisible({ timeout: 15_000 })
  await expect(rowB).toContainText(a.fullName)
  await rowB.getByText(fileA).dblclick()
  await expect(pageB.getByText('Báo cáo của người gửi')).toBeVisible({ timeout: 15_000 })
  await pageB.getByRole('button', { name: 'Đóng' }).first().click()
  const renamed = `bao-cao-B-da-sua-${stamp}.txt`
  await contextMenu(pageB, fileA)
  await pageB.getByRole('menuitem', { name: / Đổi tên/ }).click()
  const box = pageB.getByRole('dialog').getByRole('textbox')
  await box.press('Control+A')
  await box.pressSequentially(renamed)
  await pageB.getByRole('dialog').getByRole('button', { name: /Lưu|Đồng ý|OK|Đổi tên/ }).click()
  await expect(pageB.getByRole('row', { name: new RegExp(renamed) })).toBeVisible()
  // A sees the rename in their own drive.
  await pageA.goto('/my-drive')
  await expect(pageA.getByRole('row', { name: new RegExp(renamed) })).toBeVisible()

  // B → A view only.
  await pageB.goto('/my-drive')
  await shareWith(pageB, fileB, a, /^Xem$/)
  await pageA.goto('/shared')
  const rowA = pageA.getByRole('row', { name: new RegExp(fileB) })
  await expect(rowA).toBeVisible({ timeout: 15_000 })
  await rowA.getByText(fileB).dblclick()
  await expect(pageA.getByText('Kế hoạch của người nhận')).toBeVisible({ timeout: 15_000 })
  await pageA.getByRole('button', { name: 'Đóng' }).first().click()
  await contextMenu(pageA, fileB)
  await expect(pageA.getByRole('menuitem', { name: / Đổi tên/ })).toHaveCount(0)
  await expect(pageA.getByRole('menuitem', { name: / Xóa/ })).toHaveCount(0)
  await pageA.keyboard.press('Escape')
  // The API refuses an edit too.
  const forbidden = await api.put(`/api/v1/files/${fileBId}/name`, {
    headers: bearer(a),
    data: { name: 'khong-duoc-doi.txt' },
  })
  expect(forbidden.status()).toBeGreaterThanOrEqual(403)

  // A revokes the share → B no longer sees nor opens the file.
  const shares = await (await api.get(`/api/v1/shares?resourceType=file&resourceId=${fileAId}`, { headers: bearer(a) })).json()
  for (const s of shares.items ?? shares) expect((await api.delete(`/api/v1/shares/${s.id}`, { headers: bearer(a) })).ok()).toBeTruthy()
  await pageB.goto('/shared')
  await pageB.waitForLoadState('networkidle')
  await expect(pageB.getByRole('row', { name: new RegExp(renamed) })).toHaveCount(0)
  expect((await api.get(`/api/v1/files/${fileAId}`, { headers: bearer(b) })).status()).toBe(404)

  await pageA.context().close()
  await pageB.context().close()
})
