import { expect, test, type APIRequestContext, type Page } from '@playwright/test'
import { openContextMenu } from './helpers'

/**
 * Every item of the file context menu opens what it should, without console errors
 * (#23/#24, #26/#27, #30, #46–48). Test data is created through the API and purged afterwards.
 */
const stamp = Date.now().toString().slice(-6)
const fileName = `menu-ngu-canh-${stamp}.txt`
let api: APIRequestContext
let auth: Record<string, string>
let folderId = ''
let targetId = ''

test.beforeAll(async ({ playwright, baseURL }) => {
  api = await playwright.request.newContext({ baseURL })
  const login = await (await api.post('/api/v1/auth/login', {
    data: { username: process.env.E2E_USERNAME ?? 'admin', password: process.env.E2E_PASSWORD ?? 'Admin@Smoke2026' },
  })).json()
  auth = { Authorization: `Bearer ${login.accessToken}` }
  const ws = (await (await api.get('/api/v1/auth/me', { headers: auth })).json()).personalWorkspaceId
  folderId = (await (await api.post('/api/v1/folders', { headers: auth, data: { workspaceId: ws, name: `E2E Menu ${stamp}` } })).json()).id
  targetId = (await (await api.post('/api/v1/folders', { headers: auth, data: { workspaceId: ws, parentId: folderId, name: 'Đích' } })).json()).id
  const up = await api.post('/api/v1/files/upload', {
    headers: auth,
    multipart: { file: { name: fileName, mimeType: 'text/plain', buffer: Buffer.from('Nội dung kiểm thử menu') }, workspaceId: ws, folderId },
  })
  expect(up.ok()).toBeTruthy()
})

test.afterAll(async () => {
  await api.post('/api/v1/items/delete', { headers: auth, data: { items: [{ type: 'folder', id: folderId }] } })
  await api.post('/api/v1/trash/purge', { headers: auth, data: [{ type: 'folder', id: folderId }] })
  await api.dispose()
})

function watchErrors(page: Page) {
  const errors: string[] = []
  page.on('console', (m) => {
    if (m.type() === 'error' && !/favicon|ResizeObserver|status of 40[134]/.test(m.text())) errors.push(m.text())
  })
  page.on('pageerror', (e) => errors.push(e.message))
  return errors
}

async function openMenu(page: Page, item: string) {
  await openContextMenu(page, page.getByRole('row', { name: new RegExp(item) }).getByText(item))
}

async function clickMenu(page: Page, item: string, name: RegExp) {
  await openMenu(page, item)
  await page.getByRole('menuitem', { name }).click()
}

/** Destination tree of "Sao chép tới / Di chuyển tới": search without diacritics, then pick the node. */
async function pickFolder(page: Page, name: string) {
  await page.getByRole('dialog').locator('.ant-tree-select').click()
  await page.keyboard.type(name.normalize('NFD').replace(/\p{M}/gu, '').replace(/đ/gi, 'd'))
  await page.locator('.ant-select-tree').getByTitle(name, { exact: true }).click()
  await expect(page.getByRole('dialog').getByTitle(name, { exact: true })).toBeVisible()
}

test('menu ngữ cảnh: mọi thao tác trên tệp', async ({ page }) => {
  const errors = watchErrors(page)
  await page.goto(`/my-drive?folder=${folderId}`)
  await page.waitForLoadState('networkidle')

  // Thông tin / Phiên bản / Thảo luận: details drawer with tabs.
  await clickMenu(page, fileName, / Thông tin$/)
  // Info opens the side panel of the listing.
  await expect(page.getByText('Ai có quyền truy cập')).toBeVisible()
  const drawer = page.locator('.ant-drawer-content')
  await clickMenu(page, fileName, / Phiên bản$/)
  await expect(drawer.getByRole('tab', { name: /Phiên bản/, selected: true })).toBeVisible()
  await page.keyboard.press('Escape')
  await clickMenu(page, fileName, / Thảo luận$/)
  await expect(drawer.getByRole('tab', { name: /Thảo luận/, selected: true })).toBeVisible()
  await page.keyboard.press('Escape')

  // Quản lý quyền truy cập.
  await clickMenu(page, fileName, /Quản lý quyền truy cập/)
  await expect(page.getByRole('dialog')).toBeVisible()
  await page.getByRole('dialog').getByRole('button', { name: /Close|Đóng/ }).first().click()
  await expect(page.getByRole('dialog')).toHaveCount(0)

  // Chia sẻ.
  await clickMenu(page, fileName, /Chia sẻ$/)
  await expect(page.getByRole('dialog')).toBeVisible()
  await page.getByRole('dialog').getByRole('button', { name: /Close|Đóng/ }).first().click()
  await expect(page.getByRole('dialog')).toHaveCount(0)

  // Tải xuống: a real download.
  const download = page.waitForEvent('download')
  await clickMenu(page, fileName, / Tải xuống$/)
  expect((await download).suggestedFilename()).toBe(fileName)

  // Mở: text viewer, closed with the close button.
  await clickMenu(page, fileName, / Mở$/)
  await expect(page.getByText('Nội dung kiểm thử menu')).toBeVisible({ timeout: 15_000 })
  await page.getByRole('button', { name: 'Đóng' }).first().click()
  await expect(page.getByText('Nội dung kiểm thử menu')).toHaveCount(0)

  // Đổi tên (F2 dialog).
  const renamed = `doi-ten-${stamp}.txt`
  await clickMenu(page, fileName, / Đổi tên/)
  const box = page.getByRole('dialog').getByRole('textbox')
  await box.press('Control+A')
  await box.pressSequentially(renamed)
  await page.getByRole('dialog').getByRole('button', { name: /Lưu|Đồng ý|OK|Đổi tên/ }).click()
  await expect(page.getByRole('row', { name: new RegExp(renamed) })).toBeVisible()

  // Sao chép tới... → folder picker → copy lands in "Đích".
  await clickMenu(page, renamed, /Sao chép tới/)
  await pickFolder(page, 'Đích')
  await Promise.all([
    page.waitForResponse((r) => r.url().includes('/items/copy') && r.ok()),
    page.getByRole('dialog').getByRole('button', { name: /Sao chép|Chọn|OK|Đồng ý/ }).last().click(),
  ])
  // Di chuyển tới... → same picker, moves the original.
  await clickMenu(page, renamed, /Di chuyển tới/)
  await pickFolder(page, 'Đích')
  await Promise.all([
    page.waitForResponse((r) => r.url().includes('/items/move') && r.ok()),
    page.getByRole('dialog').getByRole('button', { name: /Di chuyển|Chọn|OK|Đồng ý/ }).last().click(),
  ])
  await expect(page.getByRole('row', { name: new RegExp(renamed) })).toHaveCount(0)
  const inTarget = await (await api.get(`/api/v1/workspaces/${(await (await api.get('/api/v1/auth/me', { headers: auth })).json()).personalWorkspaceId}/items?folderId=${targetId}`, { headers: auth })).json()
  expect((inTarget.items ?? inTarget).filter((i: { name: string }) => i.name.startsWith('doi-ten-')).length).toBe(2)

  // Xóa (from the folder "Đích").
  // The checkbox selects without opening; a click on the row opens the folder.
  await page.getByRole('row', { name: /Đích/ }).getByRole('checkbox').check()
  await expect(page).toHaveURL(new RegExp(`folder=${folderId}`))
  await expect(page.getByRole('button', { name: /Bỏ chọn/ })).toBeVisible()
  await page.getByRole('row', { name: /Đích/ }).getByText('Đích').click()
  await expect(page).toHaveURL(new RegExp(`folder=${targetId}`))
  await page.waitForLoadState('networkidle')
  await page.getByRole('row', { name: new RegExp(renamed) }).first().getByRole('checkbox').check()
  await page.keyboard.press('Delete')
  await page.getByRole('button', { name: /Xóa|Đồng ý|OK/ }).last().click()
  await expect(page.getByRole('row', { name: new RegExp(renamed) })).toHaveCount(1)

  expect(errors).toEqual([])
})

test('ảnh mới tải lên tự hiện ảnh thu nhỏ ở chế độ lưới (không cần tải lại trang)', async ({ page }) => {
  await page.goto(`/my-drive?folder=${folderId}`)
  await page.waitForLoadState('networkidle')
  await page.getByTitle('Dạng lưới').first().click()
  const name = `anh-moi-${stamp}.png`
  const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==', 'base64')
  const chooser = page.waitForEvent('filechooser')
  await page.getByRole('main').getByRole('button', { name: /Tải lên$/ }).first().click()
  const tệp = page.getByRole('menuitem', { name: /Tải lên tệp/ })
  if (await tệp.isVisible().catch(() => false)) await tệp.click()
  await (await chooser).setFiles({ name, mimeType: 'image/png', buffer: png })
  // The worker renders the thumbnail a few seconds after the upload: the tile switches from the icon to it.
  const tile = page.locator('.bd-thumb').filter({ has: page.locator('img') })
  await expect(tile).toHaveCount(1, { timeout: 30_000 })
})
