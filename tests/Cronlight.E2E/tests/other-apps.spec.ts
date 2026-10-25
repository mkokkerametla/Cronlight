import { expect, test } from './fixtures'
import { insertForeignJob } from './foreign-jobs'
import { createJob, jobRow, jobTypes, openDashboard, searchJobs, statCard, uniqueName } from './helpers'

test('a job from another app sharing the scheduler is shown read-only', async ({ page, request, sampleHost }) => {
  const group = uniqueName('shared')
  const ours = `${group}-ours`
  await createJob(request, { type: jobTypes.plain, name: ours, group, triggers: [{ cronExpression: '0 0 6 * * ?' }] })
  const remove = insertForeignJob(sampleHost.database, group, 'report')

  try {
    await openDashboard(page)
    await searchJobs(page, group)
    const foreign = jobRow(page, 'report')

    await expect(foreign).toContainText('Unloadable')
    await expect(foreign.getByRole('button')).toHaveCount(0)
    await expect(foreign.getByRole('checkbox')).toBeDisabled()
    await expect(statCard(page, 'Unloadable')).not.toHaveText(/^0/)

    // Select all takes only the jobs this app can manage
    await page.getByRole('checkbox', { name: 'Select all' }).click()
    await expect(page.getByText('1 selected')).toBeVisible()
    await page.getByRole('button', { name: 'Clear selection' }).click()

    await foreign.click()
    const details = page.getByRole('dialog', { name: 'report' })
    await expect(details.getByText('Read-only in this dashboard')).toBeVisible()
    await expect(details).toContainText('type not loaded')
    await expect(details).toContainText('OtherApp')
    await expect(details.getByRole('button', { name: /Run now|Edit|Delete/ })).toHaveCount(0)
    await page.keyboard.press('Escape')

    await statCard(page, 'Unloadable').click()
    await expect(foreign).toBeVisible()
    await expect(jobRow(page, ours)).toHaveCount(0)

    // The API refuses too, so the read-only view is not just hidden buttons
    const edit = await request.put(`api/jobs/${group}/report`, { data: { description: 'x' } })
    expect(edit.status()).toBe(409)
    expect(await edit.text()).toContain('read-only')
    const pause = await request.post(`api/jobs/${group}/report/pause`)
    expect(pause.status()).toBe(409)
    expect(await pause.text()).toContain('read-only')
  } finally {
    remove()
  }
})
