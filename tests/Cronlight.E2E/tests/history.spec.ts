import { expect, test } from '@playwright/test'
import { createJob, jobRow, jobTypes, openDashboard, runCount, triggerJob, uniqueName } from './helpers'

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

test('moves between a job and its runs, and pages back through older runs', async ({ page, request }) => {
  const name = uniqueName('many-runs')
  await createJob(request, { type: jobTypes.plain, name })
  // One more run than a page holds
  for (let i = 0; i < 51; i++) await triggerJob(request, 'DEFAULT', name)
  await expect.poll(() => runCount(request, 'DEFAULT', name, 'Succeeded'), { timeout: 30_000 }).toBe(51)
  await openDashboard(page)

  await jobRow(page, name).click()
  await page.getByRole('dialog', { name }).getByRole('button', { name: /All runs/ }).click()

  await expect(page.getByRole('dialog')).toBeHidden()
  await expect(page.getByRole('tab', { name: /History/ })).toHaveAttribute('aria-selected', 'true')
  await expect(page.getByText(`DEFAULT.${name}`)).toBeVisible()
  const runs = page.getByRole('tabpanel').getByRole('row').filter({ hasText: name })
  await expect(runs).toHaveCount(50)

  await page.getByRole('button', { name: 'Load older runs' }).click()
  await expect(runs).toHaveCount(51)
  await expect(page.getByRole('button', { name: 'Load older runs' })).toBeHidden()

  // A run's job name opens that job
  await runs.first().getByRole('button', { name }).click()
  await expect(page.getByRole('dialog', { name })).toBeVisible()
  await page.keyboard.press('Escape')

  // Clearing the chip shows every job's runs again
  await page.getByRole('button', { name: 'Show all jobs' }).click()
  await expect(page.getByText(`DEFAULT.${name}`)).toBeHidden()
})

test('an HTTP job refuses a host that is not in its allow list', async ({ page, request }) => {
  const name = uniqueName('http-denied')
  await createJob(request, { type: jobTypes.http, name, data: { url: 'https://example.com/hook', method: 'GET' } })
  await openDashboard(page)

  await jobRow(page, name).getByRole('button', { name: `Run ${name} now` }).click()
  await page.getByRole('tab', { name: /History/ }).click()

  const run = page.getByRole('row').filter({ hasText: name })
  await expect(run).toContainText('Failed', { timeout: 20_000 })
  await expect(run).toContainText('Host example.com is not in HttpJob:AllowedHosts')
})
