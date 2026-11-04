import { expect, type Locator, type Page } from '@playwright/test'

/**
 * Right-click `target` and wait until its context menu can be used. The menu may re-align (flip above
 * the cursor) right after opening, which made a direct click land outside and close it: retry, then settle.
 * `firstItem` is an entry the menu must show (an item menu has "Thông tin"); `position` clicks a point inside `target`.
 */
export async function openContextMenu(page: Page, target: Locator, firstItem: RegExp = /Thông tin/, position?: { x: number; y: number }) {
  await expect(target).toBeVisible({ timeout: 15_000 })
  await expect(async () => {
    await page.keyboard.press('Escape')
    await target.click({ button: 'right', position })
    await expect(page.getByRole('menuitem', { name: firstItem })).toBeVisible({ timeout: 2_000 })
  }).toPass({ timeout: 15_000 })
  await page.waitForTimeout(300)
}
