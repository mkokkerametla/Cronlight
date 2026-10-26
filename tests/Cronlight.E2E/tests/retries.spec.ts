import { expect, test } from './fixtures'
import { choose, createJob, jobRow, jobTypes, openDashboard, runCount, triggerJob, uniqueName } from './helpers'

test('a job given retries in the editor is retried, and history shows each attempt', async ({ page, request }) => {
  const name = uniqueName('retried')
  await createJob(request, { type: jobTypes.flaky, name, data: { failureRate: '100' } })
  await openDashboard(page)

  await jobRow(page, name).click()
  const details = page.getByRole('dialog', { name })
  await details.getByRole('button', { name: 'Edit' }).click()
  await details.getByLabel('Retries', { exact: true }).fill('2')
  await details.getByLabel('Wait before retrying (seconds)').fill('1')
  await expect(details.getByText(/^Up to 2 retries, 1s apart\./)).toBeVisible()
  await details.getByRole('button', { name: 'Save changes' }).click()
  await expect(details.getByText('Up to 2 retries, 1s apart', { exact: true })).toBeVisible()

  await details.getByRole('button', { name: 'Run now' }).click()
  // Two attempts that are retried, then the last one fails
  await expect.poll(() => runCount(request, 'DEFAULT', name, 'Retried'), { timeout: 30_000 }).toBe(2)
  await expect.poll(() => runCount(request, 'DEFAULT', name, 'Failed'), { timeout: 30_000 }).toBe(1)

  await details.getByRole('button', { name: /All runs/ }).click()
  const runs = page.getByRole('tabpanel').getByRole('row').filter({ hasText: name })
  await expect(runs).toHaveCount(3)
  const last = runs.filter({ hasText: 'attempt 3' })
  await expect(last).toContainText('Failed')
  await expect(last).toContainText('Upstream API returned 503 Service Unavailable')
  // The status badge, matched exactly: hasText ignores case and the job's name contains "retried"
  const retried = runs.filter({ has: page.getByText('Retried', { exact: true }) })
  await expect(retried).toHaveCount(2)
  await expect(runs.filter({ hasText: 'attempt 2' })).toContainText('Retried')
  // Only the last attempt can be run again: the others were retried already
  await expect(last.getByRole('button', { name: `Run ${name} again` })).toBeVisible()
  await expect(retried.getByRole('button', { name: `Run ${name} again` })).toHaveCount(0)
})

test('runs a job once with different data, leaving its saved data unchanged', async ({ page, request }) => {
  const name = uniqueName('run-with-data')
  await createJob(request, { type: jobTypes.flaky, name, data: { failureRate: '100' } })
  await openDashboard(page)

  await jobRow(page, name).click()
  const details = page.getByRole('dialog', { name })
  await details.getByRole('button', { name: 'Run with different data' }).click()
  await expect(details.getByText('Nothing changed')).toBeVisible()
  await details.locator('#param-failureRate').fill('0')
  await expect(details.getByText('1 changed')).toBeVisible()
  await details.getByRole('button', { name: 'Run now' }).click()

  await expect(page.getByText(`${name}: triggered with different data`)).toBeVisible()
  // The saved job always fails, so a success means the run used the changed value
  await expect(details.getByText('Succeeded')).toBeVisible({ timeout: 20_000 })
  const job = await (await request.get(`api/jobs/DEFAULT/${name}`)).json()
  expect(job.data.failureRate).toBe('100')

  // The next plain run uses the saved data again
  await details.getByRole('button', { name: 'Run now' }).click()
  await expect.poll(() => runCount(request, 'DEFAULT', name, 'Failed'), { timeout: 20_000 }).toBe(1)
  expect(await runCount(request, 'DEFAULT', name, 'Succeeded')).toBe(1)
})

test('runs a failed run again from the History tab', async ({ page, request }) => {
  const name = uniqueName('run-again')
  await createJob(request, { type: jobTypes.flaky, name, data: { failureRate: '100' } })
  await triggerJob(request, 'DEFAULT', name)
  await expect.poll(() => runCount(request, 'DEFAULT', name, 'Failed'), { timeout: 20_000 }).toBe(1)
  await openDashboard(page)

  await page.getByRole('tab', { name: /History/ }).click()
  await choose(page, page.getByLabel('Filter by outcome'), 'Failed')
  const runs = page.getByRole('row').filter({ hasText: name })
  await runs.getByRole('button', { name: `Run ${name} again` }).click()

  await expect(page.getByText(`${name}: triggered again`)).toBeVisible()
  await expect(runs).toHaveCount(2, { timeout: 20_000 })
})

test.describe('viewer role', () => {
  test.use({ extraHTTPHeaders: { 'X-Demo-Role': 'viewer', 'X-Cronlight-Request': '1' } })

  test('cannot run a failed run again', async ({ page, playwright, baseURL }) => {
    // Arrange as admin, since a viewer cannot create or run jobs
    const admin = await playwright.request.newContext({ baseURL, extraHTTPHeaders: { 'X-Demo-Role': 'admin', 'X-Cronlight-Request': '1' } })
    const name = uniqueName('viewer-run-again')
    await createJob(admin, { type: jobTypes.flaky, name, data: { failureRate: '100' } })
    await triggerJob(admin, 'DEFAULT', name)
    await expect.poll(() => runCount(admin, 'DEFAULT', name, 'Failed'), { timeout: 20_000 }).toBe(1)
    await admin.dispose()
    await openDashboard(page)

    await page.getByRole('tab', { name: /History/ }).click()
    const run = page.getByRole('row').filter({ hasText: name })
    await expect(run).toContainText('Failed')
    await expect(run.getByRole('button', { name: `Run ${name} again` })).toHaveCount(0)
  })
})
