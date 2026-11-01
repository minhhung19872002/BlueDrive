import { expect, test as setup } from '@playwright/test'

const username = process.env.E2E_USERNAME ?? 'admin'
const password = process.env.E2E_PASSWORD ?? 'Admin@Smoke2026'

setup('đăng nhập quản trị', async ({ page }) => {
  await page.goto('/login')
  await page.getByLabel(/tên đăng nhập/i).fill(username)
  await page.getByLabel(/mật khẩu/i).fill(password)
  await page.getByRole('button', { name: /đăng nhập/i }).click()
  await expect(page).toHaveURL(/\/$/, { timeout: 20_000 })
  await page.context().storageState({ path: 'e2e/.auth/admin.json' })
})
