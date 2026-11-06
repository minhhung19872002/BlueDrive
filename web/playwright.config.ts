import { defineConfig, devices } from '@playwright/test'

/**
 * E2E tests run against a running stack (docker compose or dev servers).
 *   E2E_BASE_URL   default http://localhost:8088
 *   E2E_USERNAME / E2E_PASSWORD   an administrator whose password is already changed
 *   E2E_DOC_SCREENSHOTS=1   refresh the user guide screenshots in docs/images (off by default: test data must not end up in the docs)
 *   E2E_EDGE=1   also run the cross-browser suite in Microsoft Edge (must be installed on the machine)
 *
 * Quality 4.1 (browsers): the full suite runs in Chromium; the screens and core flows also run in Firefox and WebKit (Safari):
 *   npx playwright test --project=firefox --project=webkit
 */
const desktop = { viewport: { width: 1440, height: 900 }, storageState: 'e2e/.auth/admin.json' }
/** Screens and core flows. The other specs cover Chromium-only integrations (desktop app, ONLYOFFICE Desktop) or create accounts. */
const crossBrowser = /(pages|flows|context-menu|ops)\.spec\.ts/

export default defineConfig({
  testDir: './e2e',
  timeout: 60_000,
  expect: { timeout: 10_000 },
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [['list'], ['html', { open: 'never', outputFolder: 'playwright-report' }]],
  use: {
    baseURL: process.env.E2E_BASE_URL ?? 'http://localhost:8088',
    locale: 'vi-VN',
    timezoneId: 'Asia/Ho_Chi_Minh',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [
    { name: 'setup', testMatch: /auth\.setup\.ts/ },
    {
      name: 'desktop',
      use: { ...devices['Desktop Chrome'], ...desktop },
      dependencies: ['setup'],
      testIgnore: /mobile\.spec\.ts/,
    },
    {
      name: 'mobile',
      use: { ...devices['Pixel 7'], storageState: 'e2e/.auth/admin.json' },
      dependencies: ['setup'],
      testMatch: /mobile\.spec\.ts/,
    },
    { name: 'firefox', use: { ...devices['Desktop Firefox'], ...desktop }, dependencies: ['setup'], testMatch: crossBrowser },
    { name: 'webkit', use: { ...devices['Desktop Safari'], ...desktop }, dependencies: ['setup'], testMatch: crossBrowser },
    ...(process.env.E2E_EDGE
      ? [{ name: 'edge', use: { ...devices['Desktop Edge'], channel: 'msedge', ...desktop }, dependencies: ['setup'], testMatch: crossBrowser }]
      : []),
  ],
})
