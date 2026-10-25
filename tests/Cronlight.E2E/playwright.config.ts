import os from 'node:os'
import { defineConfig, devices } from '@playwright/test'

// Each worker runs a sample host and a Chromium, about 0.7 GB together, and both busy a core while tests
// run. One worker per 4 GB of memory and per 2 cores, at most 4, keeps the machine usable: 2 on a 12 GB
// laptop and on a GitHub runner, 4 on a large workstation. E2E_WORKERS overrides it.
const gigabytes = os.totalmem() / 1024 ** 3
const workers =
  Number(process.env.E2E_WORKERS) ||
  Math.max(1, Math.min(4, Math.floor(gigabytes / 4), Math.floor(os.availableParallelism() / 2)))

// End-to-end tests drive the sample host (samples/Cronlight.SampleHost) in a real browser. It is built once
// (global-setup.ts), then each worker starts its own on a fresh SQLite file (tests/fixtures.ts), with
// view/manage policies so permissions are exercised: requests carry X-Demo-Role (admin by default), which
// the sample turns into a role claim.
export default defineConfig({
  testDir: './tests',
  globalSetup: './global-setup.ts',
  // Each worker has its own host and database, so files run in parallel; tests in a file run in order
  workers,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI ? [['list'], ['html', { open: 'never' }]] : 'list',
  timeout: 60_000,
  expect: { timeout: 10_000 },
  use: {
    // baseURL comes from the worker's sample host
    // The API refuses changes without X-Cronlight-Request; tests that arrange jobs through it send it too
    extraHTTPHeaders: { 'X-Demo-Role': 'admin', 'X-Cronlight-Request': '1' },
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
})
