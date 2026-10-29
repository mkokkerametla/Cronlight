import AxeBuilder from '@axe-core/playwright'
import type { Page } from '@playwright/test'
import { expect, test } from './fixtures'
import { createJob, jobRow, jobTypes, openDashboard, uniqueName } from './helpers'

/**
 * What axe finds against WCAG 2.2 A and AA on the page, as each rule and where it failed. Colours are judged once
 * fades and colour transitions have ended (a tab changing colour, a panel sliding in); endless ones, such as a
 * spinner, are left running.
 */
async function problems(page: Page) {
  await page.waitForFunction(() =>
    document
      .getAnimations()
      .every(a => a.playState !== 'running' || a.effect?.getTiming().iterations === Infinity)
  )
  const { violations } = await new AxeBuilder({ page })
    .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'])
    .analyze()
  return violations.map(v => `${v.id}: ${v.nodes.map(n => n.target.join(' ')).join(' | ')}`)
}

test('the job list, a job, its editor and New job meet WCAG 2.2 AA as axe checks it', async ({ page, request }) => {
  const name = uniqueName('a11y')
  await createJob(request, { type: jobTypes.plain, name, triggers: [{ cronExpression: '0 0 6 * * ?' }] })
  await openDashboard(page)
  await expect(jobRow(page, name)).toBeVisible()
  expect(await problems(page)).toEqual([])

  await jobRow(page, name).getByRole('button', { name, exact: true }).click()
  const panel = page.getByRole('dialog', { name })
  await expect(panel).toBeVisible()
  expect(await problems(page)).toEqual([])

  await panel.getByRole('button', { name: 'Edit' }).click()
  await expect(panel.getByRole('button', { name: 'Save changes' })).toBeVisible()
  expect(await problems(page)).toEqual([])
  await panel.getByRole('button', { name: 'Cancel' }).click()
  await page.keyboard.press('Escape')
  await expect(panel).toBeHidden()

  await page.getByRole('button', { name: 'New job' }).click()
  const form = page.getByRole('dialog', { name: 'New job' })
  await expect(form).toBeVisible()
  expect(await problems(page)).toEqual([])
})

test('Running and History meet WCAG 2.2 AA as axe checks them', async ({ page }) => {
  await openDashboard(page)

  await page.getByRole('tab', { name: /Running/ }).click()
  await expect(page.getByRole('tab', { name: /Running/ })).toHaveAttribute('aria-selected', 'true')
  expect(await problems(page)).toEqual([])

  await page.getByRole('tab', { name: /History/ }).click()
  await expect(page.getByRole('region', { name: 'Run statistics' })).toBeVisible()
  expect(await problems(page)).toEqual([])
})

test('the dark theme keeps the same contrast', async ({ page, request }) => {
  await createJob(request, { type: jobTypes.plain, name: uniqueName('a11y-dark'), triggers: [{ cronExpression: '0 0 6 * * ?' }] })
  await page.emulateMedia({ colorScheme: 'dark' })
  await openDashboard(page)
  await expect(page.locator('html')).toHaveClass(/dark/)

  expect(await problems(page)).toEqual([])
})
