import { expect, test } from '@playwright/test'
import { choose, createJob, jobRow, jobTypes, openDashboard, uniqueName } from './helpers'

test('creates a job from the form, with parameter validation and a cron preview', async ({ page }) => {
  const name = uniqueName('invoices')
  await openDashboard(page)
  await page.getByRole('button', { name: 'New job' }).click()
  const sheet = page.getByRole('dialog', { name: 'New job' })

  await choose(page, sheet.locator('#job-type'), /Send invoices/)
  await sheet.getByLabel('Name', { exact: true }).fill(name)
  await sheet.getByLabel('Group', { exact: true }).fill('billing')
  await sheet.getByLabel('Cron expression').fill('0 0 9 ? * MON-FRI')
  await expect(sheet.getByText('Next (your local time)')).toBeVisible()

  // customerId is a required [JobParameter]: the browser blocks the submit until it is filled
  const customerId = sheet.locator('#param-customerId')
  await sheet.getByRole('button', { name: 'Create job' }).click()
  expect(await customerId.evaluate(input => (input as HTMLInputElement).validity.valueMissing)).toBe(true)
  await expect(sheet).toBeVisible()

  await customerId.fill('42')
  await sheet.locator('#param-smtpPassword').fill('hunter2')
  await sheet.getByRole('button', { name: 'Create job' }).click()

  await expect(sheet).toBeHidden()
  const row = jobRow(page, name)
  await expect(row).toContainText('billing')
  await expect(row).toContainText('Send invoices')

  // Secrets never come back from the API
  await row.click()
  const details = page.getByRole('dialog', { name })
  await expect(details).toContainText('42')
  await expect(details).not.toContainText('hunter2')
})

test('rejects an invalid cron expression before saving', async ({ page }) => {
  await openDashboard(page)
  await page.getByRole('button', { name: 'New job' }).click()
  const sheet = page.getByRole('dialog', { name: 'New job' })
  await choose(page, sheet.locator('#job-type'), /PlainJob/)
  await sheet.getByLabel('Name', { exact: true }).fill(uniqueName('bad-cron'))

  await sheet.getByLabel('Cron expression').fill('every day please')
  // The preview checks the expression with Quartz as you type...
  await expect(sheet.getByText("Illegal characters for this position: 'EVE'")).toBeVisible()

  // ...and the server refuses to save it
  await sheet.getByRole('button', { name: 'Create job' }).click()
  await expect(page.getByText("Invalid cron expression 'every day please'")).toBeVisible()
  await expect(sheet).toBeVisible()
})

test('edits the schedule of a job and keeps it paused', async ({ page, request }) => {
  const name = uniqueName('edit')
  await createJob(request, { type: jobTypes.plain, name, triggers: [{ name: `${name}-t`, cronExpression: '0 0 6 * * ?' }] })
  await openDashboard(page)

  await jobRow(page, name).getByRole('button', { name: `Pause ${name}` }).click()
  await expect(jobRow(page, name)).toContainText('Paused')

  await jobRow(page, name).click()
  const details = page.getByRole('dialog', { name })
  await details.getByRole('button', { name: 'Edit' }).click()
  await details.getByLabel('Cron expression').fill('0 30 7 * * ?')
  await details.getByRole('button', { name: 'Save changes' }).click()

  await expect(details.getByText('0 30 7 * * ?')).toBeVisible()
  // The details sheet is modal; close it to read the table behind it
  await page.keyboard.press('Escape')
  await expect(jobRow(page, name)).toContainText('Paused')
})

test('pauses, resumes and deletes a job', async ({ page, request }) => {
  const name = uniqueName('lifecycle')
  await createJob(request, { type: jobTypes.plain, name, triggers: [{ cronExpression: '0 0 6 * * ?' }] })
  await openDashboard(page)
  const row = jobRow(page, name)

  await row.getByRole('button', { name: `Pause ${name}` }).click()
  await expect(row).toContainText('Paused')
  await row.getByRole('button', { name: `Resume ${name}` }).click()
  await expect(row).not.toContainText('Paused')

  await row.click()
  const details = page.getByRole('dialog', { name })
  await details.getByRole('button', { name: 'Delete' }).click()
  await page.getByRole('alertdialog').getByRole('button', { name: 'Delete job' }).click()

  await expect(details).toBeHidden()
  await expect(row).toHaveCount(0)
})
