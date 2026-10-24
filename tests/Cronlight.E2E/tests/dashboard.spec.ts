import { expect, test } from '@playwright/test'
import { createJob, jobRow, jobTypes, openDashboard, uniqueName } from './helpers'

test('shows the scheduler, its jobs and the main tabs', async ({ page, request }) => {
  const name = uniqueName('overview')
  await createJob(request, { type: jobTypes.plain, name, group: 'e2e', triggers: [{ cronExpression: '0 0 6 * * ?' }] })

  await openDashboard(page)

  await expect(page.getByRole('heading', { name: 'SampleHost' })).toBeVisible()
  await expect(page.getByRole('tab', { name: /Running/ })).toBeVisible()
  await expect(page.getByRole('tab', { name: /History/ })).toBeVisible()
  const row = jobRow(page, name)
  await expect(row).toContainText('e2e')
  await expect(row).toContainText('PlainJob')
})

test('search narrows the job list', async ({ page, request }) => {
  const wanted = uniqueName('search-hit')
  const other = uniqueName('search-miss')
  await createJob(request, { type: jobTypes.plain, name: wanted })
  await createJob(request, { type: jobTypes.plain, name: other })
  await openDashboard(page)

  await page.getByLabel('Search jobs').fill(wanted)
  await expect(jobRow(page, wanted)).toBeVisible()
  await expect(jobRow(page, other)).toHaveCount(0)

  await page.getByLabel('Search jobs').fill('no-job-has-this-name')
  await expect(page.getByText('No jobs match these filters.')).toBeVisible()
})

test('theme switch applies dark mode and remembers it', async ({ page }) => {
  await openDashboard(page)

  await page.getByRole('button', { name: 'Change theme' }).click()
  await page.getByRole('menuitemradio', { name: /Dark/ }).click()
  await expect(page.locator('html')).toHaveClass(/dark/)

  await page.reload()
  await expect(page.locator('html')).toHaveClass(/dark/)
})
