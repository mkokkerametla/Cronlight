import { expect, test } from './fixtures'
import { choose, createJob, jobRow, jobTypes, openDashboard, statCard, uniqueName } from './helpers'

test('a running job shows under Running until it finishes', async ({ page, request }) => {
  const name = uniqueName('slow')
  await createJob(request, { type: jobTypes.slow, name, data: { seconds: '8' } })
  await openDashboard(page)

  await jobRow(page, name).getByRole('button', { name: `Run ${name} now` }).click()
  await statCard(page, 'Running now').click()

  await expect(page.getByRole('tab', { name: /Running/ })).toHaveAttribute('aria-selected', 'true')
  // The sample keeps jobs in a database, so running jobs are read from every node
  await expect(page.getByText('All nodes')).toBeVisible()
  const row = page.getByRole('row').filter({ hasText: name })
  // Running jobs show the class name; the running API has no display names
  await expect(row).toContainText('SlowJob')
  await expect(row).toContainText(/\d+s/)
  await expect(statCard(page, 'Running now')).not.toHaveText(/^0/)

  // Gone with the first refresh after it finishes
  await expect(row).toHaveCount(0, { timeout: 15_000 })
})

test('cancels a running job, and history records who cancelled it', async ({ page, request }) => {
  const name = uniqueName('cancel')
  await createJob(request, { type: jobTypes.slow, name })
  await openDashboard(page)

  await jobRow(page, name).getByRole('button', { name: `Run ${name} now` }).click()
  await page.getByRole('tab', { name: /Running/ }).click()
  const row = page.getByRole('row').filter({ hasText: name })
  await row.getByRole('button', { name: `Cancel ${name}` }).click()

  const confirm = page.getByRole('alertdialog')
  await expect(confirm).toContainText(`Cancel this run of ${name}?`)
  await confirm.getByRole('button', { name: 'Cancel run' }).click()
  await expect(page.getByText(`${name}: cancellation requested`)).toBeVisible()
  await expect(row).toHaveCount(0)

  // History records the outcome just after the run leaves the running list, so wait for it. SlowJob checks
  // its cancellation token, so it stopped well before its 15 seconds.
  const lastRun = async () => {
    const history = await request.get(`api/history?${new URLSearchParams({ group: 'DEFAULT', name })}`)
    return ((await history.json()) as { records: { status: string; error: string; durationMs: number }[] }).records[0]
  }
  await expect.poll(async () => (await lastRun()).status).toBe('Cancelled')
  const record = await lastRun()
  expect(record.error).toBe('Cancelled by demo')
  expect(record.durationMs).toBeLessThan(15_000)

  await page.getByRole('tab', { name: /History/ }).click()
  await choose(page, page.getByLabel('Filter by outcome'), 'Cancelled')
  const run = page.getByRole('row').filter({ hasText: name })
  await expect(run).toContainText('Cancelled by demo')
  // A cancelled run can be started again from here
  await expect(run.getByRole('button', { name: `Run ${name} again` })).toBeVisible()
})
