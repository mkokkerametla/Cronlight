import { expect, test } from '@playwright/test'
import { choose, createJob, jobRow, jobTypes, openDashboard, uniqueName } from './helpers'

test('creating a job with a name already in use shows the error and keeps the form', async ({ page, request }) => {
  const name = uniqueName('taken')
  await createJob(request, { type: jobTypes.plain, name })
  await openDashboard(page)

  await page.getByRole('button', { name: 'New job' }).click()
  const sheet = page.getByRole('dialog', { name: 'New job' })
  await choose(page, sheet.locator('#job-type'), /PlainJob/)
  await sheet.getByLabel('Name', { exact: true }).fill(name)
  await sheet.getByRole('button', { name: 'Create job' }).click()

  await expect(page.getByText(`Job DEFAULT.${name} already exists`)).toBeVisible()
  await expect(sheet).toBeVisible()
  await expect(sheet.getByLabel('Name', { exact: true })).toHaveValue(name)
})

test('changes made elsewhere show up without a reload', async ({ page, request }) => {
  const name = uniqueName('elsewhere')
  await openDashboard(page)

  // Created by someone else: appears with the next refresh, every 10 seconds
  await createJob(request, { type: jobTypes.plain, name, triggers: [{ cronExpression: '0 0 6 * * ?' }] })
  await expect(jobRow(page, name)).toBeVisible({ timeout: 15_000 })

  await jobRow(page, name).click()
  const details = page.getByRole('dialog', { name })
  await request.delete(`api/jobs/DEFAULT/${name}`)

  // Acting on a job that is gone reports it...
  await details.getByRole('button', { name: 'Pause' }).click()
  await expect(page.getByText('404 Not Found')).toBeVisible()
  // ...and the next refresh closes its sheet and drops it from the list
  await expect(details).toBeHidden({ timeout: 15_000 })
  await expect(jobRow(page, name)).toHaveCount(0)
})

test('a lost connection keeps the last data on screen until the scheduler is back', async ({ page, request }) => {
  const name = uniqueName('offline')
  await createJob(request, { type: jobTypes.plain, name })
  await openDashboard(page)
  await expect(jobRow(page, name)).toBeVisible()

  await page.route('**/api/**', route => route.abort('connectionrefused'))
  const banner = page.getByText('Lost connection to the scheduler')
  await expect(banner).toBeVisible({ timeout: 15_000 })
  await expect(jobRow(page, name)).toBeVisible()

  await page.unroute('**/api/**')
  await page.getByRole('button', { name: 'Try again' }).click()
  await expect(banner).toBeHidden()
  await expect(jobRow(page, name)).toBeVisible()
})
