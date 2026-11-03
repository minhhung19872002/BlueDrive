import { createHash, randomBytes } from 'node:crypto'
import { expect, test } from '@playwright/test'

/**
 * BlueDrive Desktop sign-in (Sync-Phase 1): the consent page sends the browser back to the app's loopback address with a
 * one-time code, the app exchanges it (PKCE) for a device session, the device shows up in "Thiết bị đã kết nối" and can be revoked.
 */
test('đăng nhập BlueDrive Desktop qua trình duyệt, xem và thu hồi thiết bị', async ({ page, request }) => {
  const port = 50000 + Math.floor(Math.random() * 9000)
  const redirect = `http://127.0.0.1:${port}/callback`
  const verifier = randomBytes(48).toString('base64url')
  const challenge = createHash('sha256').update(verifier).digest('base64url')
  const deviceName = `PC-E2E-${Date.now().toString().slice(-5)}`

  // The desktop app is not running here: answer its loopback page ourselves and capture the code.
  let callback = ''
  await page.route(`http://127.0.0.1:${port}/**`, async (route) => {
    callback = route.request().url()
    await route.fulfill({ status: 200, contentType: 'text/html', body: '<p>OK</p>' })
  })

  await page.goto(
    `/connect/authorize?client_id=bluedrive-desktop&redirect_uri=${encodeURIComponent(redirect)}&code_challenge=${challenge}` +
      `&code_challenge_method=S256&state=e2e&device_name=${encodeURIComponent(deviceName)}`,
  )
  await expect(page.getByText(deviceName)).toBeVisible()
  await page.getByRole('button', { name: 'Cho phép' }).click()
  await expect.poll(() => callback, { timeout: 15_000 }).toContain('code=')
  const url = new URL(callback)
  expect(url.searchParams.get('state')).toBe('e2e')

  const token = await request.post('/api/v1/connect/token', {
    form: {
      grant_type: 'authorization_code',
      code: url.searchParams.get('code')!,
      code_verifier: verifier,
      redirect_uri: redirect,
      client_id: 'bluedrive-desktop',
      device_name: deviceName,
      device_os: 'Windows 11',
      app_version: '1.0.0',
      machine_id: `e2e-${deviceName}`,
    },
  })
  expect(token.ok(), await token.text()).toBeTruthy()
  const body = await token.json()
  expect(body.token_type).toBe('Bearer')
  const roots = await request.get('/api/v1/sync/roots', { headers: { Authorization: `Bearer ${body.access_token}` } })
  expect(roots.ok()).toBeTruthy()

  await page.goto('/devices')
  const row = page.getByRole('row', { name: new RegExp(deviceName) })
  await expect(row).toBeVisible()
  await expect(row).toContainText('Đang hoạt động')
  await row.getByRole('button', { name: /Thu hồi/ }).click()
  await page.getByRole('button', { name: 'Thu hồi' }).last().click()
  await expect(row).toContainText('Đã thu hồi')
  const after = await request.get('/api/v1/sync/roots', { headers: { Authorization: `Bearer ${body.access_token}` } })
  expect(after.status()).toBe(401)
})
