import { expect, test } from './fixtures'
import { choose, jobRow, jobTypes, openDashboard, uniqueName } from './helpers'

// The sample host serves a second, in-memory scheduler named Reports, which it builds in code
test('switches to another scheduler, which has its own jobs, running view and history', async ({ page, request }) => {
  const name = uniqueName('monthly')
  const created = await request.post('api/jobs?scheduler=Reports', { data: { type: jobTypes.plain, name } })
  expect(created.status(), await created.text()).toBe(201)
  await openDashboard(page)
  const picker = page.getByRole('combobox', { name: 'Scheduler' })
  await expect(picker).toHaveText('SampleHost')
  await expect(jobRow(page, name)).toHaveCount(0)

  await choose(page, picker, 'Reports')
  await expect(page.getByRole('heading', { name: 'Reports' })).toBeVisible()
  await expect(page).toHaveURL(/[?&]scheduler=Reports/)
  await jobRow(page, name).getByRole('button', { name: `Run ${name} now` }).click()

  await page.getByRole('tab', { name: /History/ }).click()
  await expect(page.getByRole('row').filter({ hasText: name })).toContainText('Succeeded')
  // An in-memory scheduler: only this node's running jobs can be seen
  await page.getByRole('tab', { name: /Running/ }).click()
  await expect(page.getByText('This node only')).toBeVisible()

  // The choice survives a reload, and the host's own scheduler drops it from the URL
  await page.reload()
  await expect(page.getByRole('heading', { name: 'Reports' })).toBeVisible()
  await choose(page, picker, 'SampleHost')
  await expect(page.getByRole('heading', { name: 'SampleHost' })).toBeVisible()
  await expect(page).not.toHaveURL(/scheduler=/)
  await expect(jobRow(page, name)).toHaveCount(0)
})
