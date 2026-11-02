import { expect, test, type Page } from '@playwright/test'

/** Main business flows through the real UI (needs the docker stack incl. ONLYOFFICE). */
const stamp = Date.now().toString().slice(-6)

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

    await page.getByRole('row', { name: new RegExp(folder) }).getByText(folder).dblclick()
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

    await fileRow.getByText(fileName).click({ button: 'right' })
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
    await page.getByRole('row', { name: new RegExp(folder) }).getByText(folder).click()
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
})

test('tạo tài liệu Word mới và mở trình soạn thảo ONLYOFFICE', async ({ page }) => {
  await openMyDrive(page)
  await page.getByRole('main').getByRole('button', { name: /Mới$/ }).first().click()
  await page.getByRole('menuitem', { name: /Word/ }).click()
  // Type key by key like a user: the pre-selected default name must not swallow what is typed.
  const docName = `Kế hoạch E2E ${stamp}`
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
  await docRow.getByText(`${docName}.docx`).click({ button: 'right' })
  await page.getByRole('menuitem', { name: /Phiên bản$/ }).click()
  await expect(page.locator('.ant-drawer').getByRole('tab', { name: 'Phiên bản', selected: true })).toBeVisible()
  await page.keyboard.press('Escape')
  await expect(page.locator('.ant-drawer-open')).toHaveCount(0)
  // Opening an Office document goes straight to the editor.
  await docRow.getByText(`${docName}.docx`).dblclick()
  await expect(page).toHaveURL(/\/files\/.+\/edit/)
})

test('chia sẻ qua liên kết công khai có mật khẩu', async ({ page, browser }) => {
  await openMyDrive(page)
  const row = page.getByRole('row').filter({ hasText: /\.(txt|docx)/ }).first()
  await row.click({ button: 'right' })
  await page.getByRole('menuitem', { name: /Chia sẻ$/ }).click()
  await page.getByRole('tab', { name: /liên kết/i }).click()
  await page.getByPlaceholder(/mật khẩu/i).fill('1234')
  await page.getByRole('button', { name: /Tạo liên kết/ }).click()
  const linkBox = page.getByRole('dialog').locator('input[value*="/s/"]').first()
  await expect(linkBox).toBeVisible()
  const link = await linkBox.inputValue()
  const path = new URL(link!.trim(), 'http://x').pathname
  const anon = await browser.newContext({ storageState: { cookies: [], origins: [] } })
  const pub = await anon.newPage()
  await pub.goto(path)
  await pub.getByPlaceholder(/mật khẩu/i).fill('1234')
  await pub.getByRole('button', { name: /Mở|Xem|Tiếp tục/ }).click()
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
  await page.getByPlaceholder(/Tìm|mã|tên/i).first().fill(`kiem thu tu dong ${stamp}`)
  await page.keyboard.press('Enter')
  await expect(page.getByText(`Phòng Kiểm thử Tự động ${stamp}`)).toBeVisible({ timeout: 10_000 })
})
