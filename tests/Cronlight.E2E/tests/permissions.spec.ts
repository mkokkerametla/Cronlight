import { expect, test } from '@playwright/test'
import { createJob, jobRow, jobTypes, openDashboard, uniqueName } from './helpers'

test.describe('viewer role', () => {
  test.use({ extraHTTPHeaders: { 'X-Demo-Role': 'viewer', 'X-Cronlight-Request': '1' } })

  test('sees jobs read-only, without any controls that change them', async ({ page, playwright, baseURL }) => {
    // Arrange as admin, since a viewer cannot create jobs
    const admin = await playwright.request.newContext({ baseURL, extraHTTPHeaders: { 'X-Demo-Role': 'admin', 'X-Cronlight-Request': '1' } })
    const name = uniqueName('viewer-sees')
    await createJob(admin, { type: jobTypes.plain, name, triggers: [{ cronExpression: '0 0 6 * * ?' }] })
    await admin.dispose()

    await openDashboard(page)

    await expect(page.getByText('Read-only', { exact: true })).toBeVisible()
    await expect(page.getByRole('button', { name: 'New job' })).toHaveCount(0)
    const row = jobRow(page, name)
    await expect(row).toBeVisible()
    await expect(row.getByRole('button')).toHaveCount(0)

    await row.click()
    const details = page.getByRole('dialog', { name })
    await expect(details.getByRole('button', { name: 'Edit' })).toHaveCount(0)
    await expect(details.getByRole('button', { name: 'Delete' })).toHaveCount(0)
  })

  test('is refused by the API when it tries to change a job', async ({ request }) => {
    const response = await request.post('api/jobs', { data: { type: jobTypes.plain, name: uniqueName('nope') } })
    expect(response.status()).toBe(403)
  })
})

test.describe('no role', () => {
  test.use({ extraHTTPHeaders: {} })

  test('cannot open the dashboard', async ({ page }) => {
    const response = await page.goto('./')
    expect([401, 403]).toContain(response?.status())
  })
})
