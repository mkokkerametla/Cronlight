import { expect, test } from './fixtures'
import { createJob, jobRow, jobTypes, openDashboard, runCount, uniqueName } from './helpers'

test('a cron schedule runs the job by itself, and pausing stops it', async ({ page, request }) => {
  const name = uniqueName('every-2s')
  await createJob(request, { type: jobTypes.plain, name, triggers: [{ cronExpression: '0/2 * * * * ?' }] })

  try {
    // Nobody presses Run now: runs come from the trigger alone
    await expect.poll(() => runCount(request, 'DEFAULT', name), { timeout: 15_000 }).toBeGreaterThanOrEqual(2)

    await openDashboard(page)
    await jobRow(page, name).click()
    const details = page.getByRole('dialog', { name })
    await expect(details.getByText('Succeeded').first()).toBeVisible()
    await page.keyboard.press('Escape')

    await jobRow(page, name).getByRole('button', { name: `Pause ${name}` }).click()
    await expect(jobRow(page, name)).toContainText('Paused')
    // Paused means the count settles: two readings more than two fire intervals apart agree. Still
    // firing, it would grow between every pair; a run that started just before the pause only delays this.
    let last = -1
    await expect
      .poll(
        async () => {
          const count = await runCount(request, 'DEFAULT', name)
          const settled = count === last
          last = count
          return settled
        },
        { intervals: [4500], timeout: 25_000 }
      )
      .toBe(true)
  } finally {
    await request.delete(`api/jobs/DEFAULT/${name}`)
  }
})

test('removing every trigger keeps the job, which then runs only by hand', async ({ page, request }) => {
  const name = uniqueName('drop-triggers')
  await createJob(request, { type: jobTypes.plain, name, triggers: [{ cronExpression: '0 0 6 * * ?' }] })
  await openDashboard(page)

  await jobRow(page, name).click()
  const details = page.getByRole('dialog', { name })
  await details.getByRole('button', { name: 'Edit' }).click()
  await details.getByRole('button', { name: 'Remove trigger' }).click()
  await details.getByRole('button', { name: 'Save changes' }).click()

  await expect(details.getByText('No triggers. This job only runs when triggered manually.')).toBeVisible()
  await expect(details.locator('dt', { hasText: 'Durable' }).locator('xpath=following-sibling::dd[1]')).toHaveText('Yes')

  // Still there after a reload, so it was kept on the server and not just in the page. The address keeps the job open.
  await page.reload()
  await expect(page.getByRole('dialog', { name })).toContainText('No triggers.')
  await page.keyboard.press('Escape')
  await expect(jobRow(page, name)).toBeVisible()
  await expect(jobRow(page, name)).toContainText('-')
})
