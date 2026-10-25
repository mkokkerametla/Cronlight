import { spawn } from 'node:child_process'
import { rm } from 'node:fs/promises'
import os from 'node:os'
import path from 'node:path'
import { test as base } from '@playwright/test'
import { sampleHostDir, sampleHostDll } from '../global-setup'

export { expect } from '@playwright/test'

/** The sample host a worker's tests run against, and the SQLite file it keeps its jobs and history in. */
export interface SampleHost {
  baseURL: string
  database: string
}

const basePort = Number(process.env.E2E_PORT ?? 5199)

/**
 * Each worker starts its own sample host, on its own port and database, so workers run side by side
 * without seeing each other's jobs or history. Within a worker, tests run one after another and share
 * the host, so they use their own job names. A worker that hits a failure is replaced, with a new host.
 */
export const test = base.extend<object, { sampleHost: SampleHost }>({
  sampleHost: [
    async ({}, use, workerInfo) => {
      const port = basePort + workerInfo.parallelIndex
      const database = path.join(os.tmpdir(), `cronlight-e2e-${process.pid}-${workerInfo.workerIndex}.db`)
      const host = spawn('dotnet', [sampleHostDll], {
        cwd: sampleHostDir,
        stdio: ['ignore', 'ignore', 'pipe'],
        env: {
          ...process.env,
          ASPNETCORE_ENVIRONMENT: 'Development',
          ASPNETCORE_URLS: `http://127.0.0.1:${port}`,
          ConnectionStrings__Quartz: `Data Source=${database}`,
          Cronlight__ViewPolicy: 'QuartzViewers',
          Cronlight__ManagePolicy: 'QuartzAdmins',
          // Views refresh every second instead of every 10, so tests wait less for changes to show
          Cronlight__RefreshSeconds: '1',
        },
      })
      let stderr = ''
      host.stderr.on('data', chunk => (stderr += chunk))
      const exited = new Promise<void>(resolve => host.once('exit', () => resolve()))

      try {
        await waitUntilServing(`http://127.0.0.1:${port}/`, exited, () => stderr)
        await use({ baseURL: `http://127.0.0.1:${port}/ops/jobs/`, database })
      } finally {
        host.kill('SIGTERM')
        // The sample waits for running jobs on shutdown; a long one is not worth waiting for
        const killed = setTimeout(() => host.kill('SIGKILL'), 5_000)
        await exited
        clearTimeout(killed)
        await Promise.all(['', '-wal', '-shm'].map(suffix => rm(database + suffix, { force: true })))
      }
    },
    { scope: 'worker', timeout: 60_000 },
  ],
  baseURL: async ({ sampleHost }, use) => use(sampleHost.baseURL),
})

async function waitUntilServing(url: string, exited: Promise<void>, stderr: () => string) {
  let stopped = false
  void exited.then(() => (stopped = true))
  const deadline = Date.now() + 50_000
  while (Date.now() < deadline) {
    if (stopped) throw new Error(`The sample host exited while starting:\n${stderr()}`)
    try {
      // Any answer will do: "/" redirects to the dashboard, which then asks for a role
      await fetch(url, { redirect: 'manual' })
      return
    } catch {
      await new Promise(resolve => setTimeout(resolve, 200))
    }
  }
  throw new Error(`The sample host did not answer on ${url} within 50 seconds:\n${stderr()}`)
}
