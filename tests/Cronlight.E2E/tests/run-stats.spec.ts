import type { APIRequestContext } from '@playwright/test'
import { expect, test } from './fixtures'
import { createJob, jobRow, jobTypes, openDashboard, runCount, searchJobs, triggerJob, uniqueName } from './helpers'

async function runAndWait(request: APIRequestContext, name: string, status: string, count: number) {
  await triggerJob(request, 'DEFAULT', name)
  await expect.poll(() => runCount(request, 'DEFAULT', name, status), { timeout: 20_000 }).toBe(count)
}

test('a job’s success rate and durations show on its panel and above its History', async ({ page, request }) => {
  const name = uniqueName('stats')
  await createJob(request, { type: jobTypes.flaky, name, data: { failureRate: '0' } })
  await runAndWait(request, name, 'Succeeded', 1)
  await runAndWait(request, name, 'Succeeded', 2)
  const response = await request.put(`api/jobs/DEFAULT/${name}`, { data: { data: { failureRate: '100' } } })
  expect(response.status(), await response.text()).toBe(204)
  await runAndWait(request, name, 'Failed', 1)

  await openDashboard(page)
  await searchJobs(page, name)
  await jobRow(page, name).click()
  const panel = page.getByRole('dialog', { name })
  const figures = panel.getByRole('table')
  await expect(figures.getByRole('row', { name: /^Runs/ })).toHaveText(/Runs\s*3\s*3/)
  await expect(figures.getByRole('row', { name: /^Success rate/ })).toHaveText(/66\.6%\s*66\.6%/)
  await expect(panel.getByRole('img', { name: /^Durations of the last 3 runs, oldest first: .* succeeded, .* succeeded, .* failed$/ })).toBeVisible()

  await panel.getByRole('button', { name: /All runs/ }).click()
  const stats = page.getByRole('region', { name: 'Run statistics' })
  const figure = (label: string) => stats.getByText(label, { exact: true }).locator('xpath=following-sibling::dd')
  await expect(figure('Runs')).toHaveText('3')
  await expect(figure('Success rate')).toHaveText('66.6%')
  await expect(figure('Failures')).toHaveText('1')
})
