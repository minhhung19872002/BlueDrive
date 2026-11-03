import { expect, type Locator, type Page } from '@playwright/test'

/**
 * Right-click `target` and wait until its context menu can be used. The menu may re-align (flip above
 * the cursor) right after opening, which made a direct click land outside and close it: retry, then settle.
 */
export async function openContextMenu(page: Page, target: Locator) {
  await expect(target).toBeVisible({ timeout: 15_000 })
  await expect(async () => {
    await page.keyboard.press('Escape')
    await target.click({ button: 'right' })
    await expect(page.getByRole('menuitem', { name: /Thông tin/ })).toBeVisible({ timeout: 2_000 })
  }).toPass({ timeout: 15_000 })
  await page.waitForTimeout(300)
}
