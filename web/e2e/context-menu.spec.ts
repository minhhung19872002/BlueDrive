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
let ws = ''

test.beforeAll(async ({ playwright, baseURL }) => {
  api = await playwright.request.newContext({ baseURL })
  const login = await (await api.post('/api/v1/auth/login', {
    data: { username: process.env.E2E_USERNAME ?? 'admin', password: process.env.E2E_PASSWORD ?? 'Admin@Smoke2026' },
  })).json()
  auth = { Authorization: `Bearer ${login.accessToken}` }
  ws = (await (await api.get('/api/v1/auth/me', { headers: auth })).json()).personalWorkspaceId
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
  // Out of the trash, then out of the second stage (spec 8.1).
  for (let i = 0; i < 2; i++) await api.post('/api/v1/trash/purge', { headers: auth, data: [{ type: 'folder', id: folderId }] })
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

/** "Sao chép tới / Di chuyển tới" (SharePoint-like picker): search the current folder without diacritics, then open the folder. */
async function pickFolder(page: Page, name: string) {
  const dialog = page.getByRole('dialog')
  await dialog.getByPlaceholder('Tìm trong thư mục hiện tại').fill(name.normalize('NFD').replace(/\p{M}/gu, '').replace(/đ/gi, 'd'))
  await dialog.getByRole('button', { name, exact: true }).click()
  await expect(dialog.locator('.bd-picker-crumbs strong', { hasText: name })).toBeVisible()
}

test('menu ngữ cảnh: mọi thao tác trên tệp', async ({ page }) => {
  const errors = watchErrors(page)
  await page.goto(`/my-drive?folder=${folderId}`)
  await page.waitForLoadState('networkidle')

  // Thông tin / Phiên bản / Thảo luận: details drawer with tabs.
  await clickMenu(page, fileName, / Thông tin$/)
  // Info opens the details drawer over the listing (like "Phiên bản"); the list keeps its width.
  const drawer = page.locator('.ant-drawer-content')
  await expect(drawer.getByRole('tab', { name: /Thông tin/, selected: true })).toBeVisible()
  await expect(drawer.getByText('Ai có quyền truy cập')).toBeVisible()
  await page.keyboard.press('Escape')
  await expect(drawer).toHaveCount(0)
  await clickMenu(page, fileName, / Phiên bản$/)
  await expect(drawer.getByRole('tab', { name: /Phiên bản/, selected: true })).toBeVisible()
  await page.keyboard.press('Escape')
  await clickMenu(page, fileName, / Thảo luận$/)
  await expect(drawer.getByRole('tab', { name: /Thảo luận/, selected: true })).toBeVisible()
  await page.keyboard.press('Escape')

  // Chia sẻ: one dialog for who has access, general access and the link (no separate "Quản lý quyền truy cập").
  await clickMenu(page, fileName, /Chia sẻ$/)
  await expect(page.getByRole('dialog')).toBeVisible()
  await expect(page.getByRole('dialog').getByRole('combobox', { name: 'Quyền truy cập chung' })).toBeVisible()
  await expect(page.getByRole('dialog').getByRole('textbox', { name: 'Liên kết của tài liệu' })).toBeVisible({ timeout: 15_000 })
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

test('menu ngữ cảnh: Cắt / Sao chép / Dán (cả vùng trống của danh sách)', async ({ page }) => {
  const errors = watchErrors(page)
  const clipName = `cat-dan-${stamp}.txt`
  const parentName = `Cắt dán ${stamp}`
  const parent = (await (await api.post('/api/v1/folders', { headers: auth, data: { workspaceId: ws, parentId: folderId, name: parentName } })).json()).id
  const box = (await (await api.post('/api/v1/folders', { headers: auth, data: { workspaceId: ws, parentId: parent, name: 'Hộp' } })).json()).id
  const up = await api.post('/api/v1/files/upload', {
    headers: auth,
    multipart: { file: { name: clipName, mimeType: 'text/plain', buffer: Buffer.from('Cắt dán') }, workspaceId: ws, folderId: parent },
  })
  expect(up.ok()).toBeTruthy()
  const namesIn = async (id: string) =>
    ((await (await api.get(`/api/v1/workspaces/${ws}/items?folderId=${id}`, { headers: auth })).json()).items as { name: string }[]).map((i) => i.name)

  await page.goto(`/my-drive?folder=${parent}`)
  await page.waitForLoadState('networkidle')

  // Cắt the file, then "Dán vào "Hộp"" on the folder row: the file moves into "Hộp".
  await clickMenu(page, clipName, /Cắt\s*Ctrl\+X/)
  await expect(page.getByText(/Đã cắt 1 mục/)).toBeVisible()
  await openMenu(page, 'Hộp')
  await Promise.all([
    page.waitForResponse((r) => r.url().includes('/items/move') && r.ok()),
    page.getByRole('menuitem', { name: /Dán vào "Hộp" \(1 mục\)/ }).click(),
  ])
  await expect(page.getByRole('row', { name: new RegExp(clipName) })).toHaveCount(0)

  // In "Hộp": Sao chép, back to the parent folder, right-click the empty part of the list → "Dán": a copy lands here.
  await page.getByRole('row', { name: /Hộp/ }).getByText('Hộp').click()
  await expect(page).toHaveURL(new RegExp(`folder=${box}`))
  await clickMenu(page, clipName, /Sao chép\s*Ctrl\+C/)
  await expect(page.getByText(/Đã sao chép 1 mục/)).toBeVisible()
  await page.locator('.bd-explorer-breadcrumb').getByText(parentName).click()
  await expect(page).toHaveURL(new RegExp(`folder=${parent}`))
  await page.waitForLoadState('networkidle')
  await openContextMenu(page, page.locator('.bd-explorer-main'), /Dán \(1 mục\)/, { x: 40, y: 300 })
  await expect(page.getByRole('menuitem', { name: /Thư mục mới/ })).toBeVisible()
  await expect(page.getByRole('menuitem', { name: /Tải lên tệp/ })).toBeVisible()
  await Promise.all([
    page.waitForResponse((r) => r.url().includes('/items/copy') && r.ok()),
    page.getByRole('menuitem', { name: /Dán \(1 mục\)/ }).click(),
  ])
  await expect(page.getByRole('row', { name: new RegExp(clipName) })).toBeVisible()

  expect(await namesIn(box)).toContain(clipName)
  expect(await namesIn(parent)).toContain(clipName)
  expect(errors).toEqual([])
})

test('"Tìm kiếm nâng cao" từ một thư mục giữ phạm vi thư mục đó (#20/#32)', async ({ page }) => {
  const errors = watchErrors(page)
  const word = `phamvi${stamp}`
  const scoped = (await (await api.post('/api/v1/folders', { headers: auth, data: { workspaceId: ws, parentId: folderId, name: `Phạm vi ${stamp}` } })).json()).id
  for (const [name, at] of [[`${word} trong.txt`, scoped], [`${word} ngoai.txt`, folderId]] as const) {
    const up = await api.post('/api/v1/files/upload', {
      headers: auth,
      multipart: { file: { name, mimeType: 'text/plain', buffer: Buffer.from('Phạm vi tìm kiếm') }, workspaceId: ws, folderId: at },
    })
    expect(up.ok()).toBeTruthy()
  }

  await page.goto(`/my-drive?folder=${scoped}`)
  await page.waitForLoadState('networkidle')
  await page.locator('.bd-scoped-search input').fill(word)
  await page.locator('.bd-scoped-search input').press('Enter')
  await expect(page.getByText(new RegExp(`1 kết quả cho "${word}"`))).toBeVisible({ timeout: 15_000 })

  await page.getByRole('button', { name: /Tìm kiếm nâng cao/ }).click()
  await expect(page).toHaveURL(new RegExp(`/search\\?.*workspaceId=${ws}.*folderId=${scoped}`))
  await expect(page.getByText(`Chỉ trong thư mục "Phạm vi ${stamp}" và các thư mục con`)).toBeVisible()
  await expect(page.getByText(/Tìm thấy 1 kết quả/)).toBeVisible({ timeout: 15_000 })
  await expect(page.getByText(`${word} trong.txt`)).toBeVisible()

  // Removing the folder limit widens the search to the whole workspace.
  await page.getByLabel('Bỏ giới hạn thư mục').click()
  await page.locator('.bd-search-layout').getByRole('button', { name: /Tìm kiếm$/ }).click()
  await expect(page.getByText(/Tìm thấy 2 kết quả/)).toBeVisible({ timeout: 15_000 })
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
  // Scoped to the new image's tile: text files in the folder get rendered thumbnails too.
  const tile = page.locator(`.bd-tile[title="${name}"] .bd-thumb img`)
  await expect(tile).toHaveCount(1, { timeout: 30_000 })
})

test('rê chuột vào dòng: nút thao tác nhanh cạnh tên', async ({ page }) => {
  await page.goto(`/my-drive?folder=${folderId}`)
  await page.waitForLoadState('networkidle')
  const row = page.getByRole('row').filter({ hasText: /\.(txt|png)/ }).first()
  const download = row.getByRole('button', { name: 'Tải xuống' })
  await expect(download).toBeHidden()
  await row.hover()
  await expect(download).toBeVisible()
  // The link is taken from the Share dialog only (one link per item): no separate "Sao chép liên kết" next to the name.
  await expect(row.getByRole('button', { name: 'Sao chép liên kết' })).toHaveCount(0)
  await row.getByRole('button', { name: 'Chia sẻ' }).click()
  // The quick action must not open the file.
  await expect(page).toHaveURL(new RegExp(`folder=${folderId}`))
  await expect(page.getByRole('dialog')).toContainText('Chia sẻ')
})
