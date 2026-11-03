import { expect, test } from '@playwright/test'

/** "Tải ứng dụng máy tính" (Sync-Phase 9): releases.json from /downloads/desktop/, download links, mass deployment commands. */
const releases = {
  product: 'BlueDrive Desktop',
  channels: {
    stable: {
      version: '1.2.0',
      publishedAt: '2026-11-03T10:00:00Z',
      runtime: 'win-x64',
      minimumOs: 'Windows 10 1809 (17763)',
      setup: { file: 'BlueDriveSetup.exe', size: 85_000_000, sha256: 'a'.repeat(64) },
      msix: { file: 'BlueDrive.msix', size: 73_000_000, sha256: 'b'.repeat(64) },
      macos: { file: 'BlueDrive.pkg', size: 90_000_000, sha256: 'd'.repeat(64) },
      notes: 'BlueDrive Desktop 1.2.0\n- Đồng bộ nhanh hơn',
    },
    beta: {
      version: '1.3.0-beta.1',
      publishedAt: '2026-11-03T11:00:00Z',
      runtime: 'win-x64',
      minimumOs: 'Windows 10 1809 (17763)',
      setup: { file: 'BlueDriveSetup-beta.exe', size: 85_000_000, sha256: 'c'.repeat(64) },
      msix: null,
      notes: null,
    },
  },
}

test('tải ứng dụng máy tính: phiên bản, liên kết tải, kênh beta, lệnh triển khai', async ({ page }) => {
  await page.route('**/downloads/desktop/releases.json', (route) => route.fulfill({ json: releases }))
  await page.goto('/')
  await page.getByRole('button', { name: /Quản trị hệ thống|admin/ }).last().click()
  await page.getByRole('menuitem', { name: 'Tải ứng dụng máy tính' }).click()
  await expect(page).toHaveURL(/\/desktop-app$/)

  const main = page.getByRole('main')
  await expect(main.getByRole('link', { name: /Tải BlueDrive 1\.2\.0 cho Windows/ })).toHaveAttribute('href', '/downloads/desktop/BlueDriveSetup.exe')
  await expect(main.getByRole('link', { name: /Gói MSIX/ })).toHaveAttribute('href', '/downloads/desktop/BlueDrive.msix')
  await expect(main.getByRole('link', { name: /macOS \(Apple silicon\)/ })).toHaveAttribute('href', '/downloads/desktop/BlueDrive.pkg')
  await expect(main.getByText('a'.repeat(64))).toBeVisible()
  await expect(main.getByText('81,1 MB (BlueDriveSetup.exe)')).toBeVisible()

  await main.getByText('Bản thử nghiệm (beta)').click()
  await expect(main.getByRole('link', { name: /Tải bản thử nghiệm 1\.3\.0-beta\.1/ })).toHaveAttribute('href', '/downloads/desktop/BlueDriveSetup-beta.exe')

  const origin = new URL(page.url()).origin
  await expect(main.getByText(`install-bluedrive.ps1 -Server ${origin} -WithOnlyOffice 1`)).toBeVisible()
})

test('tải ứng dụng máy tính: máy chủ chưa có bản phát hành', async ({ page }) => {
  await page.route('**/downloads/desktop/releases.json', (route) => route.fulfill({ status: 404, body: '' }))
  await page.goto('/desktop-app')
  await expect(page.getByText('Chưa có bản phát hành BlueDrive Desktop trên máy chủ này')).toBeVisible()
})
