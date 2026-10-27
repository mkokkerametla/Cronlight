import { expect, test } from './fixtures'
import { choose, createJob, jobRow, jobTypes, openDashboard, uniqueName } from './helpers'

// One-time and interval times are entered and shown in the browser's time zone and locale; fixed here so the
// expected text does not depend on the machine running the tests
test.use({ locale: 'en-US', timezoneId: 'UTC' })

test('creates a job with a one-time trigger, described in the job list and its panel', async ({ page, request }) => {
  const name = uniqueName('once')
  await openDashboard(page)
  await page.getByRole('button', { name: 'New job' }).click()
  const sheet = page.getByRole('dialog', { name: 'New job' })
  await choose(page, sheet.locator('#job-type'), /PlainJob/)
  await sheet.getByLabel('Name', { exact: true }).fill(name)

  await choose(page, sheet.getByLabel('Trigger kind'), 'One time')
  await expect(sheet.getByText('Choose when it runs.')).toBeVisible()
  await sheet.getByLabel('Run at').fill('2030-01-15T09:30')
  await expect(sheet.getByText('Runs once, Jan 15, 2030, 9:30:00 AM')).toBeVisible()
  await sheet.getByRole('button', { name: 'Create job' }).click()
  await expect(sheet).toBeHidden()

  await expect(jobRow(page, name)).toContainText('Once, Jan 15, 2030, 9:30:00 AM')
  await jobRow(page, name).click()
  const details = page.getByRole('dialog', { name })
  await expect(details.getByText('One time', { exact: true })).toBeVisible()
  await expect(details.getByText('Once, Jan 15, 2030, 9:30:00 AM')).toBeVisible()
  // A single fire time has no list of upcoming ones
  await expect(details.getByRole('button', { name: 'Next 5' })).toHaveCount(0)

  const job = await (await request.get(`api/jobs/DEFAULT/${name}`)).json()
  expect(job.triggers[0].kind).toBe('Once')
  expect(new Date(job.triggers[0].startTimeUtc).toISOString()).toBe('2030-01-15T09:30:00.000Z')
})

test('creates a job with an interval trigger, lists its next five fire times, and changes its interval', async ({ page, request }) => {
  const name = uniqueName('interval')
  await openDashboard(page)
  await page.getByRole('button', { name: 'New job' }).click()
  const sheet = page.getByRole('dialog', { name: 'New job' })
  await choose(page, sheet.locator('#job-type'), /PlainJob/)
  await sheet.getByLabel('Name', { exact: true }).fill(name)

  await choose(page, sheet.getByLabel('Trigger kind'), 'Interval')
  await expect(sheet.getByText('Enter how often it runs.')).toBeVisible()
  await sheet.getByLabel('Interval', { exact: true }).fill('5')
  await expect(sheet.getByLabel('Interval unit')).toHaveText('minutes')
  await sheet.getByLabel('First run').fill('2030-01-15T09:00')
  // One run is refused: that is a one-time trigger
  await sheet.getByLabel('Number of runs').fill('1')
  await expect(sheet.getByText('An interval trigger runs at least twice. For a single run, choose One time.')).toBeVisible()
  await sheet.getByLabel('Number of runs').fill('10')
  await expect(sheet.getByText('Every 5m, starting Jan 15, 2030, 9:00:00 AM, 10 runs in all')).toBeVisible()
  await sheet.getByLabel('Trigger name').fill('every-5m')
  await sheet.getByRole('button', { name: 'Create job' }).click()
  await expect(sheet).toBeHidden()

  await expect(jobRow(page, name)).toContainText('Every 5m from Jan 15, 2030, 9:00:00 AM, 10 runs in all')
  await jobRow(page, name).click()
  const details = page.getByRole('dialog', { name })
  await expect(details.getByText('Every 5m from Jan 15, 2030, 9:00:00 AM, 10 runs in all')).toBeVisible()

  const nextFive = details.getByRole('button', { name: 'Next 5' })
  await nextFive.click()
  await expect(nextFive).toHaveAttribute('aria-expanded', 'true')
  const times = details.getByRole('list', { name: 'Next 5 fire times of every-5m' }).getByRole('listitem')
  await expect(times).toHaveText([
    /^Jan 15, 2030, 9:00:00\sAM/,
    /^Jan 15, 2030, 9:05:00\sAM/,
    /^Jan 15, 2030, 9:10:00\sAM/,
    /^Jan 15, 2030, 9:15:00\sAM/,
    /^Jan 15, 2030, 9:20:00\sAM/,
  ])

  await details.getByRole('button', { name: 'Edit' }).click()
  await expect(details.getByLabel('Interval', { exact: true })).toHaveValue('5')
  await expect(details.getByLabel('Number of runs')).toHaveValue('10')
  await details.getByLabel('Interval', { exact: true }).fill('15')
  await details.getByRole('button', { name: 'Save changes' }).click()

  await expect(details.getByText('Every 15m from Jan 15, 2030, 9:00:00 AM, 10 runs in all')).toBeVisible()
  await expect(details.getByRole('button', { name: 'Next 5' })).toHaveAttribute('aria-expanded', 'false')
  await details.getByRole('button', { name: 'Next 5' }).click()
  await expect(times).toHaveText([
    /^Jan 15, 2030, 9:00:00\sAM/,
    /^Jan 15, 2030, 9:15:00\sAM/,
    /^Jan 15, 2030, 9:30:00\sAM/,
    /^Jan 15, 2030, 9:45:00\sAM/,
    /^Jan 15, 2030, 10:00:00\sAM/,
  ])
  await page.keyboard.press('Escape')
  await expect(jobRow(page, name)).toContainText('Every 15m from Jan 15, 2030, 9:00:00 AM, 10 runs in all')

  const job = await (await request.get(`api/jobs/DEFAULT/${name}`)).json()
  expect(job.triggers).toHaveLength(1)
  expect(job.triggers[0]).toMatchObject({
    name: 'every-5m',
    kind: 'Interval',
    intervalSeconds: 900,
    repeatCount: 9,
  })
  // The start was left as it was, so it is kept
  expect(new Date(job.triggers[0].startTimeUtc).toISOString()).toBe('2030-01-15T09:00:00.000Z')
})

test('Next 5 on a cron trigger lists its next five fire times, and closes again', async ({ page, request }) => {
  const name = uniqueName('next-five')
  // Noon on New Year's Day for five years, so the five times are known in advance
  await createJob(request, {
    type: jobTypes.plain,
    name,
    triggers: [{ name: `${name}-t`, cronExpression: '0 0 12 1 1 ? 2031-2035', timeZone: 'UTC' }],
  })
  await openDashboard(page)
  await jobRow(page, name).click()
  const details = page.getByRole('dialog', { name })

  const nextFive = details.getByRole('button', { name: 'Next 5' })
  await expect(nextFive).toHaveAttribute('aria-expanded', 'false')
  await nextFive.click()
  const list = details.getByRole('list', { name: `Next 5 fire times of ${name}-t` })
  await expect(list.getByRole('listitem')).toHaveText([
    /^Jan 1, 2031, 12:00:00\sPM/,
    /^Jan 1, 2032, 12:00:00\sPM/,
    /^Jan 1, 2033, 12:00:00\sPM/,
    /^Jan 1, 2034, 12:00:00\sPM/,
    /^Jan 1, 2035, 12:00:00\sPM/,
  ])

  await nextFive.click()
  await expect(list).toBeHidden()
  await expect(nextFive).toHaveAttribute('aria-expanded', 'false')
})
