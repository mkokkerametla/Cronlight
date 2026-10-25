import os from 'node:os'
import path from 'node:path'
import { defineConfig, devices } from '@playwright/test'

// End-to-end tests drive the sample host (samples/Cronlight.SampleHost) in a real browser.
// Each run starts it on its own SQLite file, with view/manage policies so permissions are exercised:
// requests carry X-Demo-Role (admin by default), which the sample turns into a role claim.
const port = Number(process.env.E2E_PORT ?? 5199)
// The runner picks a new file for each run; workers (which load this config again) inherit its choice,
// so tests can reach the same database as the sample host
if (!process.env.TEST_WORKER_INDEX)
  process.env.CRONLIGHT_E2E_DATABASE = path.join(os.tmpdir(), `cronlight-e2e-${process.pid}.db`)
const database = process.env.CRONLIGHT_E2E_DATABASE!

export default defineConfig({
  testDir: './tests',
  // One sample host and database for all tests; specs use their own job names but share the history
  workers: 1,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI ? [['list'], ['html', { open: 'never' }]] : 'list',
  timeout: 60_000,
  expect: { timeout: 10_000 },
  use: {
    baseURL: `http://127.0.0.1:${port}/ops/jobs/`,
    // The API refuses changes without X-Cronlight-Request; tests that arrange jobs through it send it too
    extraHTTPHeaders: { 'X-Demo-Role': 'admin', 'X-Cronlight-Request': '1' },
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: {
    command: 'dotnet run --project ../../samples/Cronlight.SampleHost -c Release --no-launch-profile',
    url: `http://127.0.0.1:${port}/`,
    reuseExistingServer: false,
    timeout: 240_000,
    stdout: 'ignore',
    stderr: 'pipe',
    env: {
      ASPNETCORE_ENVIRONMENT: 'Development',
      ASPNETCORE_URLS: `http://127.0.0.1:${port}`,
      ConnectionStrings__Quartz: `Data Source=${database}`,
      Cronlight__ViewPolicy: 'QuartzViewers',
      Cronlight__ManagePolicy: 'QuartzAdmins',
    },
  },
})
