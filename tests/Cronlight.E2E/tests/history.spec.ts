import { expect, test } from '@playwright/test'
import { createJob, jobRow, jobTypes, openDashboard, uniqueName } from './helpers'

test('a failed run shows in history with its error, and the outcome filter finds it', async ({ page, request }) => {
  const name = uniqueName('always-fails')
  await createJob(request, { type: jobTypes.flaky, name, data: { failureRate: '100' } })
  await openDashboard(page)

  await jobRow(page, name).getByRole('button', { name: `Run ${name} now` }).click()

  await page.getByRole('tab', { name: /History/ }).click()
  await page.getByLabel('Filter by outcome').click()
  await page.getByRole('option', { name: 'Failed' }).click()
  const run = page.getByRole('row').filter({ hasText: name })
  // History refreshes every 10 seconds
  await expect(run).toContainText('Failed', { timeout: 20_000 })
  await expect(run).toContainText('Upstream API returned 503 Service Unavailable')
})

test('a successful run appears in the job’s recent runs', async ({ page, request }) => {
  const name = uniqueName('succeeds')
  await createJob(request, { type: jobTypes.plain, name })
  await openDashboard(page)

  await jobRow(page, name).click()
  const details = page.getByRole('dialog', { name })
  await details.getByRole('button', { name: 'Run now' }).click()

  await expect(details.getByText('Succeeded').first()).toBeVisible({ timeout: 20_000 })
})

test('an HTTP job scheduled from the UI fails on an unexpected status code', async ({ page, request, baseURL }) => {
  const name = uniqueName('http')
  const site = new URL(baseURL!).origin
  await createJob(request, { type: jobTypes.http, name, data: { url: `${site}/demo/status/500`, method: 'GET' } })
  await openDashboard(page)

  await jobRow(page, name).getByRole('button', { name: `Run ${name} now` }).click()
  await page.getByRole('tab', { name: /History/ }).click()

  const run = page.getByRole('row').filter({ hasText: name })
  await expect(run).toContainText('Failed', { timeout: 20_000 })
  await expect(run).toContainText('returned 500')
})
