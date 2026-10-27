import { expect, test } from './fixtures'
import { createJob, jobRow, jobTypes, openDashboard, searchJobs, uniqueName } from './helpers'

const daily = [{ cronExpression: '0 0 6 * * ?' }]

test('pauses and resumes a whole group from a job’s menu, after asking, and leaves other groups running', async ({ page, request }) => {
  const prefix = uniqueName('group')
  const [group, other] = [`${prefix}-billing`, `${prefix}-reports`]
  const [first, second, elsewhere] = [`${prefix}-a`, `${prefix}-b`, `${prefix}-c`]
  await createJob(request, { type: jobTypes.plain, name: first, group, triggers: daily })
  await createJob(request, { type: jobTypes.plain, name: second, group, triggers: daily })
  await createJob(request, { type: jobTypes.plain, name: elsewhere, group: other, triggers: daily })
  // Paused on its own, so resuming the group is shown to resume it too
  await request.post(`api/jobs/${group}/${second}/pause`)
  await openDashboard(page)
  await searchJobs(page, prefix)
  await expect(jobRow(page, second)).toContainText('Paused')

  // Part of the group runs and part waits, so the menu offers both
  await jobRow(page, first).getByRole('button', { name: `More actions for ${first}` }).click()
  await expect(page.getByRole('menuitem', { name: `Resume group ${group}` })).toBeVisible()
  await page.getByRole('menuitem', { name: `Pause group ${group}` }).click()
  const confirm = page.getByRole('alertdialog', { name: `Pause group ${group}?` })
  await expect(confirm).toContainText('until the group is resumed, on any node')
  // Cancelling changes nothing
  await confirm.getByRole('button', { name: 'Cancel' }).click()
  await expect(confirm).toBeHidden()
  await expect(jobRow(page, first)).toContainText('Active')

  await jobRow(page, first).getByRole('button', { name: `More actions for ${first}` }).click()
  await page.getByRole('menuitem', { name: `Pause group ${group}` }).click()
  await confirm.getByRole('button', { name: 'Pause group' }).click()
  await expect(page.getByText(`Group ${group}: paused`)).toBeVisible()
  await expect(jobRow(page, first)).toContainText('Paused')
  await expect(jobRow(page, second)).toContainText('Paused')
  await expect(jobRow(page, elsewhere)).toContainText('Active')

  // Nothing in the group runs any more, so only resuming is offered
  await jobRow(page, second).getByRole('button', { name: `More actions for ${second}` }).click()
  await expect(page.getByRole('menuitem', { name: `Pause group ${group}` })).toHaveCount(0)
  await page.getByRole('menuitem', { name: `Resume group ${group}` }).click()
  const resume = page.getByRole('alertdialog', { name: `Resume group ${group}?` })
  await expect(resume).toContainText('including jobs that were paused one by one')
  await resume.getByRole('button', { name: 'Resume group' }).click()
  await expect(page.getByText(`Group ${group}: resumed`)).toBeVisible()
  await expect(jobRow(page, first)).toContainText('Active')
  await expect(jobRow(page, second)).toContainText('Active')
  await expect(jobRow(page, elsewhere)).toContainText('Active')
})

// Every worker runs its own sample host, and a worker runs one test at a time, so pausing everything here
// cannot reach a test running elsewhere. The scheduler is resumed whatever happens, for the tests this worker
// runs next.
test('pauses and resumes every job in the scheduler from All jobs, after asking', async ({ page, request }) => {
  const group = uniqueName('all')
  const [first, second, added] = [`${group}-a`, `${group}-b`, `${group}-added`]
  await createJob(request, { type: jobTypes.plain, name: first, group, triggers: daily })
  await createJob(request, { type: jobTypes.plain, name: second, group: `${group}-other`, triggers: daily })
  await openDashboard(page)
  await searchJobs(page, group)

  try {
    await page.getByRole('button', { name: 'All jobs' }).click()
    await page.getByRole('menuitem', { name: 'Pause all jobs' }).click()
    const confirm = page.getByRole('alertdialog', { name: 'Pause every job?' })
    await expect(confirm).toContainText('on any node, and jobs added meanwhile start paused too')
    await confirm.getByRole('button', { name: 'Pause all jobs' }).click()
    await expect(page.getByText('All jobs paused')).toBeVisible()
    await expect(jobRow(page, first)).toContainText('Paused')
    await expect(jobRow(page, second)).toContainText('Paused')

    // Quartz keeps "everything paused" in the job store, so a job added now starts paused
    await createJob(request, { type: jobTypes.plain, name: added, group: `${group}-new`, triggers: daily })
    await expect(jobRow(page, added)).toContainText('Paused')

    await page.getByRole('button', { name: 'All jobs' }).click()
    await page.getByRole('menuitem', { name: 'Resume all jobs' }).click()
    const resume = page.getByRole('alertdialog', { name: 'Resume every job?' })
    await expect(resume).toContainText('including jobs that were paused one by one')
    await resume.getByRole('button', { name: 'Resume all jobs' }).click()
    await expect(page.getByText('All jobs resumed')).toBeVisible()
    await expect(jobRow(page, first)).toContainText('Active')
    await expect(jobRow(page, second)).toContainText('Active')
    await expect(jobRow(page, added)).toContainText('Active')
  } finally {
    const resumed = await request.post('api/scheduler/resume')
    expect(resumed.ok(), await resumed.text()).toBe(true)
  }
})
