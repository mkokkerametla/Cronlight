import { expect, test } from '@playwright/test'
import { createJob, jobRow, jobTypes, openDashboard, statCard, uniqueName } from './helpers'

test('a running job shows under Running until it finishes', async ({ page, request }) => {
  // SlowJob takes 15 seconds
  const name = uniqueName('slow')
  await createJob(request, { type: jobTypes.slow, name })
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

  // The tab refreshes every 10 seconds
  await expect(row).toHaveCount(0, { timeout: 35_000 })
})
