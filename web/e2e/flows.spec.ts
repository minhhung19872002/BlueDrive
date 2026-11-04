import { expect, test, type Page } from '@playwright/test'
import { openContextMenu } from './helpers'

/** Main business flows through the real UI (needs the docker stack incl. ONLYOFFICE). */
const stamp = Date.now().toString().slice(-6)
/** Word document created by the ONLYOFFICE test and reused by the link sharing test (never a user's file). */
const docName = `Kế hoạch E2E ${stamp}`

async function openMyDrive(page: Page) {
  await page.goto('/my-drive')
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
    await page.getByRole('menuitem', { name: /Đánh dấu quan trọng/ }).click()
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
    await page.getByRole('button', { name: /Xóa|Đồng ý|OK/ }).last().click()
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

test('chia sẻ qua liên kết công khai có mật khẩu', async ({ page, browser }) => {
  await openMyDrive(page)
  const row = page.getByRole('row', { name: new RegExp(`${docName}\\.docx`) })
  await openContextMenu(page, row.getByText(`${docName}.docx`))
  await page.getByRole('menuitem', { name: /Chia sẻ$/ }).click()
  await page.getByRole('tab', { name: /liên kết/i }).click()
  // Like SharePoint the link is there right away (signed-in users only by default).
  const dialog = page.getByRole('dialog')
  const linkBox = dialog.getByRole('textbox', { name: 'Liên kết của tài liệu' })
  await expect(linkBox).toHaveValue(/\/s\//, { timeout: 15_000 })
  await expect(dialog.getByText('Yêu cầu đăng nhập').last()).toBeVisible()
  const link = await linkBox.inputValue()
  // Make it public with a password: the same link is updated.
  await dialog.getByPlaceholder(/mật khẩu/i).fill('1234')
  await dialog.getByRole('switch').nth(1).click()
  await Promise.all([
    page.waitForResponse((r) => r.url().includes('/api/v1/share-links/') && r.request().method() === 'PUT' && r.ok()),
    dialog.getByRole('button', { name: /Lưu cài đặt/ }).click(),
  ])
  await expect(linkBox).toHaveValue(link)
  const path = new URL(link!.trim(), 'http://x').pathname
  const anon = await browser.newContext({ storageState: { cookies: [], origins: [] } })
  const pub = await anon.newPage()
  await pub.goto(path)
  await pub.getByPlaceholder(/mật khẩu/i).fill('1234')
  await pub.getByRole('button', { name: /Mở|Xem|Tiếp tục/ }).click()
  await expect(pub.getByText(/Chia sẻ bởi|được chia sẻ/i).first()).toBeVisible({ timeout: 15_000 })
  await anon.close()
})

test('liên kết cho phép chỉnh sửa: người không đăng nhập nhập tên rồi cùng soạn', async ({ page, browser }) => {
  await openMyDrive(page)
  const row = page.getByRole('row', { name: new RegExp(`${docName}\\.docx`) })
  await openContextMenu(page, row.getByText(`${docName}.docx`))
  await page.getByRole('menuitem', { name: /Chia sẻ$/ }).click()
  await page.getByRole('tab', { name: /liên kết/i }).click()
  const dialog = page.getByRole('dialog')
  const linkBox = dialog.getByRole('textbox', { name: 'Liên kết của tài liệu' })
  await expect(linkBox).toHaveValue(/\/s\//, { timeout: 15_000 })
  const editSwitch = dialog.getByRole('switch', { name: /Cho phép chỉnh sửa/ })
  if ((await editSwitch.getAttribute('aria-checked')) !== 'true') await editSwitch.click()
  await Promise.all([
    page.waitForResponse((r) => r.url().includes('/api/v1/share-links/') && r.request().method() === 'PUT' && r.ok()),
    dialog.getByRole('button', { name: /Lưu cài đặt/ }).click(),
  ])
  await expect(dialog.getByText('Cho phép chỉnh sửa', { exact: true })).toBeVisible()
  const path = new URL((await linkBox.inputValue()).trim(), 'http://x').pathname

  const anon = await browser.newContext({ storageState: { cookies: [], origins: [] } })
  const pub = await anon.newPage()
  await pub.goto(path)
  const password = pub.getByPlaceholder(/mật khẩu/i)
  if (await password.isVisible({ timeout: 5_000 }).catch(() => false)) {
    await password.fill('1234')
    await pub.getByRole('button', { name: /^Mở$/ }).click()
  }
  await pub.getByLabel('Họ và tên').fill('Khách kiểm thử E2E')
  const [config] = await Promise.all([
    pub.waitForResponse((r) => r.url().includes('/office/public-config/') && r.ok()),
    pub.getByRole('button', { name: /Bắt đầu chỉnh sửa/ }).click(),
  ])
  const body = await config.json()
  expect(body.mode).toBe('edit')
  expect(body.config.editorConfig.user.name).toBe('Khách kiểm thử E2E (khách)')
  await expect(pub.locator('iframe[name="frameEditor"], iframe[src*="/office/"]').first()).toBeAttached({ timeout: 45_000 })
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
  await page.getByPlaceholder(/Tìm|mã|tên/i).first().fill(`kiem thu tu dong ${stamp}`)
  await page.keyboard.press('Enter')
  await expect(page.getByText(`Phòng Kiểm thử Tự động ${stamp}`)).toBeVisible({ timeout: 10_000 })
})
