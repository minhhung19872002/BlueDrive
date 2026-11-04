import { expect, test } from '@playwright/test'

/** Every screen renders without uncaught errors; screenshots feed docs/huong-dan-su-dung.md. */
const pages: { path: string; name: string; heading?: RegExp }[] = [
  { path: '/', name: 'trang-chu' },
  { path: '/my-drive', name: 'tai-lieu-cua-toi' },
  { path: '/shared', name: 'duoc-chia-se' },
  { path: '/recent', name: 'gan-day' },
  { path: '/starred', name: 'quan-trong' },
  { path: '/trash', name: 'thung-rac' },
  { path: '/workspaces', name: 'nhom-cua-toi' },
  { path: '/search?q=dan%20so', name: 'tim-kiem' },
  { path: '/approvals', name: 'phe-duyet' },
  { path: '/reports', name: 'bao-cao-cua-toi' },
  { path: '/support', name: 'ho-tro' },
  { path: '/sessions', name: 'phien-dang-nhap' },
  { path: '/profile', name: 'thong-tin-ca-nhan' },
  { path: '/admin/dashboard', name: 'qt-dashboard' },
  { path: '/admin/organizations', name: 'qt-co-cau-to-chuc' },
  { path: '/admin/users', name: 'qt-nguoi-dung' },
  { path: '/admin/user-groups', name: 'qt-nhom-nguoi-dung' },
  { path: '/admin/roles', name: 'qt-vai-tro' },
  { path: '/admin/permission-matrix', name: 'qt-bang-doi-chieu' },
  { path: '/admin/catalogs', name: 'qt-danh-muc' },
  { path: '/admin/workspaces', name: 'qt-nhom-tai-nguyen' },
  { path: '/admin/audit-logs', name: 'qt-nhat-ky' },
  { path: '/admin/storage-stats', name: 'qt-thong-ke-dung-luong' },
  { path: '/admin/alerts', name: 'qt-canh-bao' },
  { path: '/admin/incidents', name: 'qt-su-co-attt' },
  { path: '/admin/sessions', name: 'qt-phien' },
  { path: '/admin/backups', name: 'qt-sao-luu' },
  { path: '/admin/tickets', name: 'qt-ho-tro' },
  { path: '/admin/change-logs', name: 'qt-nhat-ky-thay-doi' },
  { path: '/admin/announcements', name: 'qt-thong-bao-he-thong' },
  { path: '/admin/system-status', name: 'qt-trang-thai' },
  { path: '/admin/settings', name: 'qt-cau-hinh' },
]

for (const p of pages) {
  test(`trang ${p.path} hiển thị không lỗi`, async ({ page }, testInfo) => {
    const errors: string[] = []
    page.on('pageerror', (e) => errors.push(`pageerror: ${e.message}`))
    page.on('console', (m) => {
      if (m.type() === 'error' && !/favicon|ResizeObserver|Failed to load resource: the server responded with a status of 40[134]/.test(m.text()))
        errors.push(`console: ${m.text()}`)
    })
    await page.goto(p.path)
    await page.waitForLoadState('networkidle')
    await expect(page.locator('body')).not.toContainText(/Something went wrong|Unexpected Application Error|Đã xảy ra lỗi hệ thống/)
    await expect(page.getByText('404', { exact: true })).toHaveCount(0)
    // Only the Chromium run refreshes the documentation screenshots.
    if (testInfo.project.name === 'desktop') await page.screenshot({ path: `../docs/images/${p.name}.png`, fullPage: false })
    expect(errors, errors.join('\n')).toEqual([])
  })
}
