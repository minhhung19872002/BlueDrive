import { expect, test } from '@playwright/test'

/** Quality 5.2 (system announcements) and HSMT 3.4 (security incident records) through the real UI. */
const stamp = Date.now().toString().slice(-6)

test('thông báo hệ thống: thêm → hiện trên đầu trang → ẩn → xóa', async ({ page }) => {
  const title = `Bảo trì E2E ${stamp}`
  await page.goto('/admin/announcements')
  await page.waitForLoadState('networkidle')
  await page.getByRole('button', { name: /Thêm thông báo/ }).click()
  const dialog = page.getByRole('dialog', { name: 'Thêm thông báo hệ thống' })
  await dialog.getByLabel('Tiêu đề').fill(title)
  await dialog.getByLabel('Nội dung').fill('Hệ thống tạm dừng 15 phút để nâng cấp.')
  await dialog.getByRole('button', { name: 'Lưu' }).click()
  await expect(page.getByText('Đã lưu thông báo')).toBeVisible()

  // The banner shows on every page for every signed-in user (refreshed in realtime).
  await page.goto('/my-drive')
  const banner = page.getByRole('region', { name: 'Thông báo hệ thống' })
  await expect(banner.getByText(title)).toBeVisible()
  await expect(banner.getByText('Hệ thống tạm dừng 15 phút để nâng cấp.')).toBeVisible()
  await banner.locator('.ant-alert').filter({ hasText: title }).locator('.ant-alert-close-icon').click()
  await expect(page.getByText(title)).toHaveCount(0)
  await page.reload()
  await page.waitForLoadState('networkidle')
  await expect(page.getByText(title)).toHaveCount(0)

  // Clean up so the announcement never reaches real users of the server under test.
  await page.goto('/admin/announcements')
  const row = page.getByRole('row').filter({ hasText: title })
  await row.getByRole('button', { name: 'Xóa' }).click()
  await page.getByRole('dialog').getByRole('button', { name: 'Xóa' }).click()
  await expect(page.getByText('Đã xóa thông báo.')).toBeVisible()
  await expect(page.getByRole('row').filter({ hasText: title })).toHaveCount(0)
})

test('sự cố an toàn thông tin: hộp thoại lập hồ sơ kiểm tra dữ liệu bắt buộc', async ({ page }) => {
  // Records are not deletable (security register), so the end-to-end lifecycle is covered by the API integration tests
  // (IncidentAndAnnouncementTests) and this test stops before saving.
  await page.goto('/admin/incidents')
  await page.waitForLoadState('networkidle')
  await expect(page.getByRole('main').getByText('Sự cố an toàn thông tin').first()).toBeVisible()
  await page.getByRole('button', { name: /Lập hồ sơ sự cố/ }).first().click()
  const dialog = page.getByRole('dialog', { name: 'Lập hồ sơ sự cố an toàn thông tin' })
  await dialog.getByRole('button', { name: /Lập hồ sơ sự cố/ }).click()
  await expect(dialog.getByText('Vui lòng nhập tiêu đề sự cố.')).toBeVisible()
  await expect(dialog.getByText('Vui lòng mô tả sự cố.')).toBeVisible()
  await dialog.getByRole('button', { name: 'Hủy' }).click()
  await expect(dialog).toBeHidden()
})
