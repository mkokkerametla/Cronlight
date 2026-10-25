import { expect, test } from '@playwright/test'
import { choose, createJob, jobRow, jobTypes, openDashboard, statCard, uniqueName } from './helpers'

const daily = [{ cronExpression: '0 0 6 * * ?' }]

test('pauses and resumes several jobs at once', async ({ page, request }) => {
  const group = uniqueName('bulk')
  const [first, second] = [`${group}-a`, `${group}-b`]
  await createJob(request, { type: jobTypes.plain, name: first, group, triggers: daily })
  await createJob(request, { type: jobTypes.plain, name: second, group, triggers: daily })
  await openDashboard(page)
  await page.getByLabel('Search jobs').fill(group)

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
  await page.getByLabel('Search jobs').fill(group)

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
  await page.getByLabel('Search jobs').fill(group)

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
