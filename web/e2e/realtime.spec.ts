import { expect, test } from '@playwright/test'
import { docScreenshot } from './helpers'

/** DoD: 5 wrong passwords lock the account and the administrator sees a realtime alert (SignalR). */
test('đăng nhập sai 5 lần → tài khoản bị khóa và quản trị nhận cảnh báo thời gian thực', async ({ page, request, baseURL }) => {
  const api = `${baseURL}/api/v1`
  const login = await (await request.post(`${api}/auth/login`, {
    data: { username: process.env.E2E_USERNAME ?? 'admin', password: process.env.E2E_PASSWORD ?? 'Admin@Smoke2026' },
  })).json()
  const auth = { Authorization: `Bearer ${login.accessToken}` }
  const username = `e2elock${Date.now().toString().slice(-6)}`
  const created = await request.post(`${api}/users`, { headers: auth, data: { username, fullName: 'Kiểm thử khóa', isActive: true, password: 'Khoi@Tao12345' } })
  expect(created.ok()).toBeTruthy()

  await page.goto('/')
  await page.waitForLoadState('networkidle')
  // Give the SignalR connection a moment to start.
  await page.waitForTimeout(1500)

  let last = ''
  for (let i = 0; i < 5; i++) {
    const r = await request.post(`${api}/auth/login`, { data: { username, password: 'Sai@MatKhau1' } })
    last = (await r.json()).detail
  }
  expect(last).toContain('tạm khóa')
  const locked = await request.post(`${api}/auth/login`, { data: { username, password: 'Khoi@Tao12345' } })
  expect(locked.status()).toBe(401)

  // The alert arrives over SignalR without reloading the page.
  await expect(page.getByText(/Đăng nhập sai nhiều lần/).first()).toBeVisible({ timeout: 15_000 })
  await docScreenshot(page, 'canh-bao-thoi-gian-thuc')
})
