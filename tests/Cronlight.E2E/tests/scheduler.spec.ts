import { expect, test } from './fixtures'
import { openDashboard } from './helpers'

test('the scheduler details menu describes the scheduler and your access', async ({ page }) => {
  await openDashboard(page)
  await expect(page.getByText('Running', { exact: true })).toBeVisible()

  await page.getByRole('button', { name: 'Scheduler details' }).click()

  const menu = page.getByRole('menu')
  const value = (label: string) => menu.locator('dt', { hasText: label }).locator('xpath=following-sibling::dd[1]')
  await expect(value('Version')).toHaveText(/^\d+\.\d+/)
  await expect(value('Instance')).not.toBeEmpty()
  await expect(value('Job store')).toHaveText('JobStoreTX')
  await expect(value('Threads')).toHaveText(/^\d+$/)
  await expect(value('Your access')).toHaveText('Manage')
})
