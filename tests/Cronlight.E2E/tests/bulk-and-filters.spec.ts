import { expect, test } from './fixtures'
import { choose, createJob, jobRow, jobTypes, openDashboard, searchJobs, statCard, uniqueName } from './helpers'

const daily = [{ cronExpression: '0 0 6 * * ?' }]

test('pauses and resumes several jobs at once', async ({ page, request }) => {
  const group = uniqueName('bulk')
  const [first, second] = [`${group}-a`, `${group}-b`]
  await createJob(request, { type: jobTypes.plain, name: first, group, triggers: daily })
  await createJob(request, { type: jobTypes.plain, name: second, group, triggers: daily })
  await openDashboard(page)
  await searchJobs(page, group)

  await page.getByRole('checkbox', { name: 'Select all' }).click()
  await expect(page.getByText('2 selected')).toBeVisible()
  await page.getByRole('button', { name: 'Pause', exact: true }).click()

  await expect(page.getByText('2 jobs paused')).toBeVisible()
  await expect(jobRow(page, first)).toContainText('Paused')
  await expect(jobRow(page, second)).toContainText('Paused')
  await expect(page.getByText('2 selected')).toBeHidden()

  await page.getByRole('checkbox', { name: `Select ${first}` }).click()
  await page.getByRole('checkbox', { name: `Select ${second}` }).click()
  await page.getByRole('button', { name: 'Resume', exact: true }).click()

  await expect(page.getByText('2 jobs resumed')).toBeVisible()
  await expect(jobRow(page, first)).toContainText('Active')
  await expect(jobRow(page, second)).toContainText('Active')
})

test('filters jobs by type and by status', async ({ page, request }) => {
  const group = uniqueName('filters')
  const [invoices, plain] = [`${group}-invoices`, `${group}-plain`]
  await createJob(request, { type: jobTypes.invoice, name: invoices, group, data: { customerId: '7' }, triggers: daily })
  await createJob(request, { type: jobTypes.plain, name: plain, group, triggers: daily })
  await request.post(`api/jobs/${group}/${plain}/pause`)
  await openDashboard(page)
  await searchJobs(page, group)

  await choose(page, page.getByLabel('Filter by type'), 'Send invoices')
  await expect(jobRow(page, invoices)).toBeVisible()
  await expect(jobRow(page, plain)).toHaveCount(0)

  await choose(page, page.getByLabel('Filter by type'), 'All types')
  await choose(page, page.getByLabel('Filter by status'), 'Paused')
  await expect(jobRow(page, plain)).toBeVisible()
  await expect(jobRow(page, invoices)).toHaveCount(0)

  await page.getByRole('button', { name: 'Clear' }).click()
  await expect(jobRow(page, invoices)).toBeVisible()
  await expect(jobRow(page, plain)).toBeVisible()
})

test('the stat cards open the matching view', async ({ page, request }) => {
  const group = uniqueName('cards')
  const [active, paused] = [`${group}-on`, `${group}-off`]
  await createJob(request, { type: jobTypes.plain, name: active, group, triggers: daily })
  await createJob(request, { type: jobTypes.plain, name: paused, group, triggers: daily })
  await request.post(`api/jobs/${group}/${paused}/pause`)
  await openDashboard(page)
  await searchJobs(page, group)

  await statCard(page, 'Paused').click()
  await expect(statCard(page, 'Paused')).toHaveAttribute('aria-pressed', 'true')
  await expect(page.getByLabel('Filter by status')).toHaveText('Paused')
  await expect(jobRow(page, paused)).toBeVisible()
  await expect(jobRow(page, active)).toHaveCount(0)

  await statCard(page, 'Total jobs').click()
  await expect(jobRow(page, active)).toBeVisible()

  await statCard(page, 'Failed (24h)').click()
  await expect(page.getByRole('tab', { name: /History/ })).toHaveAttribute('aria-selected', 'true')
  await expect(page.getByLabel('Filter by outcome')).toHaveText('Failed')

  await statCard(page, 'Running now').click()
  await expect(page.getByRole('tab', { name: /Running/ })).toHaveAttribute('aria-selected', 'true')
})

test('pages through a long job list, and a new search starts again from the first page', async ({ page, request }) => {
  const group = uniqueName('paging')
  // One more job than a page holds, named so they sort in order
  const names = Array.from({ length: 51 }, (_, i) => `${group}-${String(i + 1).padStart(2, '0')}`)
  for (const name of names) await createJob(request, { type: jobTypes.plain, name, group })
  await openDashboard(page)
  await searchJobs(page, group)

  const pages = page.getByRole('navigation', { name: 'Job list pages' })
  const rows = page.getByRole('row').filter({ hasText: group })
  await expect(pages).toContainText('1–50 of 51')
  await expect(rows).toHaveCount(50)
  await expect(pages.getByRole('button', { name: 'Previous' })).toBeDisabled()

  await pages.getByRole('button', { name: 'Next' }).click()
  await expect(pages).toContainText('51–51 of 51')
  await expect(rows).toHaveCount(1)
  await expect(jobRow(page, names[50])).toBeVisible()
  await expect(pages.getByRole('button', { name: 'Next' })).toBeDisabled()

  await pages.getByRole('button', { name: 'Previous' }).click()
  await expect(pages).toContainText('1–50 of 51')
  await expect(jobRow(page, names[0])).toBeVisible()

  // From the second page, a narrower search shows its first page, and no pages once one is enough
  await pages.getByRole('button', { name: 'Next' }).click()
  await expect(pages).toContainText('51–51 of 51')
  await searchJobs(page, `${group}-5`)
  await expect(rows).toHaveCount(2)
  await expect(jobRow(page, names[49])).toBeVisible()
  await expect(jobRow(page, names[50])).toBeVisible()
  await expect(pages).toBeHidden()
})
