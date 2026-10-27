import { expect, test } from './fixtures'
import { createJob, jobRow, jobTypes, openDashboard, runCount, triggerJob, uniqueName } from './helpers'

test('what a job logs while it runs is kept with the run and shown in the History tab', async ({ page, request }) => {
  const name = uniqueName('logs')
  await createJob(request, { type: jobTypes.invoice, name, data: { customerId: '42', batchSize: '50' } })
  await openDashboard(page)

  await jobRow(page, name).getByRole('button', { name: `Run ${name} now` }).click()
  await page.getByRole('tab', { name: /History/ }).click()
  const run = page.getByRole('row').filter({ hasText: name })
  await expect(run).toContainText('Succeeded', { timeout: 20_000 })

  await run.getByRole('button', { name: 'log', exact: true }).click()
  const log = page.getByRole('list', { name: 'Run log' })
  await expect(log.getByRole('listitem').filter({ hasText: 'Invoices for customer 42, batch 50' })).toContainText('Info')

  await run.getByRole('button', { name: 'hide log' }).click()
  await expect(log).toBeHidden()
  await expect(run.getByRole('button', { name: 'log', exact: true })).toHaveAttribute('aria-expanded', 'false')
})

test('a run that logged nothing has no log to open', async ({ page, request }) => {
  const name = uniqueName('quiet')
  // SlowJob only waits, and logs nothing
  await createJob(request, { type: jobTypes.slow, name, data: { seconds: '1' } })
  await triggerJob(request, 'DEFAULT', name)
  await expect.poll(() => runCount(request, 'DEFAULT', name, 'Succeeded'), { timeout: 20_000 }).toBe(1)
  await openDashboard(page)

  await page.getByRole('tab', { name: /History/ }).click()
  const run = page.getByRole('row').filter({ hasText: name })
  await expect(run).toContainText('Succeeded')
  await expect(run.getByRole('button', { name: 'log', exact: true })).toHaveCount(0)
})

test('a failed run keeps its log, with what the HTTP client logged and the job’s secrets masked', async ({ page, request, baseURL }) => {
  const name = uniqueName('http-log')
  const apiKey = `k3y-${name}`
  // A key in the query string, as some APIs take it: the job logs the URL, so the key is in its log message
  const url = `${new URL(baseURL!).origin}/demo/status/500?apiKey=${apiKey}`
  await createJob(request, { type: jobTypes.http, name, data: { url, method: 'GET', apiKey } })
  await openDashboard(page)

  await jobRow(page, name).getByRole('button', { name: `Run ${name} now` }).click()
  await page.getByRole('tab', { name: /History/ }).click()
  const run = page.getByRole('row').filter({ hasText: name })
  await expect(run).toContainText('Failed', { timeout: 20_000 })

  await run.getByRole('button', { name: 'log', exact: true }).click()
  const log = page.getByRole('list', { name: 'Run log' })
  // From HttpClient, which the job calls: kept too, though the job did not log it itself
  await expect(log).toContainText('Start processing HTTP request GET')
  await expect(log).toContainText('/demo/status/500?apiKey=******** returned 500')
  await expect(log).not.toContainText(apiKey)
  // Through the API as well, so the key is not just hidden in the page
  const history = await request.get(`api/history?${new URLSearchParams({ group: 'DEFAULT', name })}`)
  const [record] = ((await history.json()) as { records: { id: number; hasLog: boolean }[] }).records
  expect(record.hasLog).toBe(true)
  const entries = await (await request.get(`api/history/${record.id}/log`)).text()
  expect(entries).toContain('returned 500')
  expect(entries).not.toContain(apiKey)
})
