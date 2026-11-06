import { expect, test, type Page } from '@playwright/test'
import { openContextMenu } from './helpers'

/** Main business flows through the real UI (needs the docker stack incl. ONLYOFFICE). */
const stamp = Date.now().toString().slice(-6)
/** Word document created by the ONLYOFFICE test and reused by the link sharing test (never a user's file). */
const docName = `Kế hoạch E2E ${stamp}`

/**
 * The flows create real data (folder, Word document, organization unit): remove what this run created, so that a server
 * tested again and again (production included) does not fill up with "E2E …" folders and "Phòng Kiểm thử Tự động …" units.
 */
test.afterAll(async ({ playwright, baseURL }) => {
  const api = await playwright.request.newContext({ baseURL })
  const login = await (await api.post('/api/v1/auth/login', {
    data: { username: process.env.E2E_USERNAME ?? 'admin', password: process.env.E2E_PASSWORD ?? 'Admin@Smoke2026' },
  })).json()
  const headers = { Authorization: `Bearer ${login.accessToken}` }
  const ws = (await (await api.get('/api/v1/auth/me', { headers })).json()).personalWorkspaceId
  const listed = await (await api.get(`/api/v1/workspaces/${ws}/items`, { headers, params: { q: stamp, pageSize: 100 } })).json()
  const items = ((listed.items ?? []) as { type: 'file' | 'folder'; id: string; name: string }[])
    .filter((i) => i.name.includes(stamp))
    .map((i) => ({ type: i.type, id: i.id }))
  if (items.length) {
    await api.post('/api/v1/items/delete', { headers, data: { items } })
    // Out of the trash, then out of the second stage (spec 8.1).
    for (let i = 0; i < 2; i++) await api.post('/api/v1/trash/purge', { headers, data: items })
  }
  const units = await (await api.get('/api/v1/organizations', { headers, params: { q: `E2E${stamp}`, pageSize: 20 } })).json()
  for (const u of (units.items ?? []) as { id: string; code: string }[]) if (u.code === `E2E${stamp}`) await api.delete(`/api/v1/organizations/${u.id}`, { headers })
  await api.dispose()
})

async function openMyDrive(page: Page) {
  await page.goto('/my-drive')
  await page.waitForLoadState('networkidle')
}

/** Narrows "Tài liệu của tôi" to one name: on a server used for a while the new item may be far down the list. */
async function filterMyDrive(page: Page, name: string) {
  const box = page.getByPlaceholder('Tìm trong thư mục...')
  await box.fill(name)
  await box.press('Enter')
  await page.waitForLoadState('networkidle')
}

test.describe.serial('Tư liệu cá nhân', () => {
  const folder = `E2E Dân số ${stamp}`
  const fileName = `bao-cao-dan-so-${stamp}.txt`

  test('tạo thư mục, tải tệp lên, đổi tên, đánh dấu quan trọng', async ({ page }) => {
    await openMyDrive(page)
    await page.getByRole('main').getByRole('button', { name: /Mới$/ }).first().click()
    await page.getByRole('menuitem', { name: /Thư mục/ }).click()
    await page.getByRole('dialog').getByRole('textbox').fill(folder)
    await page.getByRole('dialog').getByRole('button', { name: /Tạo|Đồng ý|OK/ }).click()
    await expect(page.getByRole('row', { name: new RegExp(folder) })).toBeVisible()

    // A single click opens the folder.
    await page.getByRole('row', { name: new RegExp(folder) }).getByText(folder).click()
    await expect(page).toHaveURL(/folder=/)
    await page.waitForLoadState('networkidle')
    const chooser = page.waitForEvent('filechooser')
    await page.getByRole('main').getByRole('button', { name: /Tải lên$/ }).first().click()
    const tệp = page.getByRole('menuitem', { name: /Tải lên tệp/ })
    if (await tệp.isVisible().catch(() => false)) await tệp.click()
    await (await chooser).setFiles({ name: fileName, mimeType: 'text/plain', buffer: Buffer.from('Báo cáo công tác dân số và phát triển') })
    // Uploads start right away: no metadata dialog (edited later in "Thông tin").
    const fileRow = page.getByRole('row', { name: new RegExp(fileName) })
    await expect(fileRow).toBeVisible({ timeout: 30_000 })
    await expect(page.getByRole('dialog')).toHaveCount(0)

    await openContextMenu(page, fileRow.getByText(fileName))
    // Leave the page only after the star is saved (navigating away earlier could cancel the request).
    await Promise.all([
      page.waitForResponse((r) => r.url().includes('/api/v1/favorites') && r.request().method() === 'PUT' && r.ok()),
      page.getByRole('menuitem', { name: /Đánh dấu quan trọng/ }).click(),
    ])
    await page.goto('/starred')
    await expect(page.getByText(fileName)).toBeVisible()
  })

  test('tìm kiếm không dấu ra tài liệu có dấu', async ({ page }) => {
    await page.goto(`/search?q=${encodeURIComponent(`dan so ${stamp}`)}`)
    await expect(page.getByText(folder).or(page.getByText(fileName)).first()).toBeVisible({ timeout: 15_000 })
  })

  test('xóa vào thùng rác và khôi phục', async ({ page }) => {
    await openMyDrive(page)
    await page.getByRole('row', { name: new RegExp(folder) }).getByRole('checkbox').check()
    await page.keyboard.press('Delete')
    await page.getByRole('button', { name: /Xóa|Đồng ý|OK/ }).last().click()
    await expect(page.getByRole('row', { name: new RegExp(folder) })).toHaveCount(0)
    await page.goto('/trash')
    await expect(page.getByRole('row', { name: new RegExp(folder) })).toBeVisible()
    await page.getByRole('row', { name: new RegExp(folder) }).getByRole('checkbox').check()
    await Promise.all([
      page.waitForResponse((r) => r.url().includes('/trash/restore') && r.ok()),
      page.getByRole('main').getByRole('button', { name: /Khôi phục$/ }).click(),
    ])
    await openMyDrive(page)
    await expect(page.getByRole('row', { name: new RegExp(folder) })).toBeVisible()
  })

  test('thùng rác hiện đúng loại tệp (.txt không hiện biểu tượng Word)', async ({ page }) => {
    await openMyDrive(page)
    await page.getByRole('row', { name: new RegExp(folder) }).getByText(folder).click()
    await page.waitForLoadState('networkidle')
    await page.getByRole('row', { name: new RegExp(fileName) }).getByRole('checkbox').check()
    await page.keyboard.press('Delete')
    await Promise.all([
      page.waitForResponse((r) => r.url().includes('/api/v1/items/delete') && r.ok()),
      page.getByRole('button', { name: /Xóa|Đồng ý|OK/ }).last().click(),
    ])
    await page.goto('/trash')
    const row = page.getByRole('row', { name: new RegExp(fileName) })
    await expect(row.getByRole('img', { name: 'TXT' })).toBeVisible()
    await row.getByRole('checkbox').check()
    await Promise.all([
      page.waitForResponse((r) => r.url().includes('/trash/restore') && r.ok()),
      page.getByRole('main').getByRole('button', { name: /Khôi phục$/ }).click(),
    ])
  })
})

test('tạo tài liệu Word mới và mở trình soạn thảo ONLYOFFICE', async ({ page }) => {
  await openMyDrive(page)
  await page.getByRole('main').getByRole('button', { name: /Mới$/ }).first().click()
  await page.getByRole('menuitem', { name: /Word/ }).click()
  // Type key by key like a user: the pre-selected default name must not swallow what is typed.
  const nameBox = page.getByRole('dialog').getByRole('textbox')
  await nameBox.press('Control+A')
  await nameBox.pressSequentially(docName)
  await expect(nameBox).toHaveValue(docName)
  await page.getByRole('dialog').getByRole('button', { name: /Tạo|Đồng ý|OK/ }).click()
  await expect(page).toHaveURL(/\/files\/.+\/edit/, { timeout: 20_000 })
  await expect(page.getByRole('main').getByText(`${docName}.docx`).first()).toBeVisible()
  // The Document Server injects an iframe for the editor.
  await expect(page.locator('iframe[name="frameEditor"], iframe[src*="/office/"]').first()).toBeAttached({ timeout: 45_000 })
  // Deep link /files/{id}/edit (reload, links in notifications) is an SPA route, not proxied to the API.
  expect((await page.reload())?.status()).toBe(200)
  await expect(page.locator('iframe[name="frameEditor"], iframe[src*="/office/"]').first()).toBeAttached({ timeout: 45_000 })
  // Close (top right, like SharePoint) leaves the editor for the folder listing.
  await page.getByRole('button', { name: 'Đóng' }).click()
  await expect(page).toHaveURL(/\/my-drive/)
  await filterMyDrive(page, docName)
  const docRow = page.getByRole('row', { name: new RegExp(`${docName}\\.docx`) })
  await expect(docRow).toBeVisible()
  // Versions / discussion live in the context menu (the viewer side panel is closed by default).
  await page.waitForLoadState('networkidle')
  await openContextMenu(page, docRow.getByText(`${docName}.docx`))
  await page.getByRole('menuitem', { name: /Phiên bản$/ }).click()
  await expect(page.locator('.ant-drawer').getByRole('tab', { name: 'Phiên bản', selected: true })).toBeVisible()
  await page.keyboard.press('Escape')
  await expect(page.locator('.ant-drawer-open')).toHaveCount(0)
  // Opening an Office document goes straight to the editor.
  await docRow.getByText(`${docName}.docx`).click()
  await expect(page).toHaveURL(/\/files\/.+\/edit/)
})

/** Opens Chia sẻ → liên kết for the E2E document; returns the dialog and the link box. */
async function openLinkTab(page: Page) {
  await openMyDrive(page)
  await filterMyDrive(page, docName)
  const row = page.getByRole('row', { name: new RegExp(`${docName}\\.docx`) })
  await openContextMenu(page, row.getByText(`${docName}.docx`))
  await page.getByRole('menuitem', { name: /Chia sẻ$/ }).click()
  const dialog = page.getByRole('dialog')
  const linkBox = dialog.getByRole('textbox', { name: 'Liên kết của tài liệu' })
  await expect(linkBox).toHaveValue(/\/s\//, { timeout: 15_000 })
  return { dialog, linkBox }
}

test('chia sẻ qua liên kết: mặc định Hạn chế, chọn "Bất kỳ ai có liên kết" thì ai có liên kết cũng mở được', async ({ page, browser }) => {
  // Like Google Drive the link is there right away, restricted (signed in and with access) by default.
  const { dialog, linkBox } = await openLinkTab(page)
  await expect(dialog.locator('.ant-select-selection-item', { hasText: /^Hạn chế$/ })).toBeVisible()
  await expect(dialog.getByPlaceholder(/mật khẩu/i)).toHaveCount(0)
  const link = await linkBox.inputValue()
  const path = new URL(link.trim(), 'http://x').pathname

  const anon = await browser.newContext({ storageState: { cookies: [], origins: [] } })
  const pub = await anon.newPage()
  await pub.goto(path)
  await expect(pub.getByText('Yêu cầu đăng nhập').first()).toBeVisible({ timeout: 15_000 })

  // "Quyền truy cập chung" → "Bất kỳ ai có liên kết": the same link opens without signing in.
  await dialog.getByRole('combobox', { name: 'Quyền truy cập chung' }).click({ force: true })
  await Promise.all([
    page.waitForResponse((r) => r.url().includes('/api/v1/share-links') && ['PUT', 'POST'].includes(r.request().method()) && r.ok()),
    page.locator('.ant-select-dropdown:visible .ant-select-item-option', { hasText: 'Bất kỳ ai có liên kết' }).click(),
  ])
  await expect(dialog.locator('.ant-select-selection-item', { hasText: 'Bất kỳ ai có liên kết' })).toBeVisible()
  await expect(linkBox).toHaveValue(link)
  await pub.reload()
  await expect(pub.getByText(/Chia sẻ bởi|được chia sẻ/i).first()).toBeVisible({ timeout: 15_000 })
  await anon.close()
})

test('quản trị: thêm đơn vị và tìm không dấu', async ({ page }) => {
  await page.goto('/admin/organizations')
  await page.getByRole('tab', { name: /bảng/i }).click()
  await page.getByRole('main').getByRole('button', { name: /Thêm/ }).first().click()
  const dlg = page.getByRole('dialog')
  await dlg.getByLabel(/^Mã/).fill(`E2E${stamp}`)
  await dlg.getByLabel(/^Tên/).fill(`Phòng Kiểm thử Tự động ${stamp}`)
  await dlg.getByRole('button', { name: /Lưu|Đồng ý|OK/ }).click()
  // The table's own box ("Tìm theo mã, tên…"), not the header's document search.
  await page.getByRole('main').getByPlaceholder(/mã, tên/i).first().fill(`kiem thu tu dong ${stamp}`)
  await page.keyboard.press('Enter')
  await expect(page.getByText(`Phòng Kiểm thử Tự động ${stamp}`)).toBeVisible({ timeout: 10_000 })
})
