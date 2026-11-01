import { expect, test } from '@playwright/test'

/** HSMT #52: the web app works on phones (375–412 px) — drawer menu, explorer, search. */
test('giao diện điện thoại: menu dạng ngăn kéo và danh sách tài liệu', async ({ page }) => {
  await page.goto('/my-drive')
  await page.waitForLoadState('networkidle')
  const scrollWidth = await page.evaluate(() => document.documentElement.scrollWidth)
  const width = page.viewportSize()!.width
  expect(scrollWidth, 'no horizontal page scroll').toBeLessThanOrEqual(width + 1)
  await page.getByRole('button', { name: /menu/i }).first().click()
  await expect(page.getByRole('menuitem', { name: /Được chia sẻ/ }).first()).toBeVisible()
  await page.screenshot({ path: '../docs/images/mobile-menu.png' })
  await page.getByRole('menuitem', { name: /Tài liệu của tôi/ }).first().click()
  await page.screenshot({ path: '../docs/images/mobile-tai-lieu.png' })
})
