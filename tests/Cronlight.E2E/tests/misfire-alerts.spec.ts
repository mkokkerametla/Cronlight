import { expect, test } from './fixtures'
import { createJob, jobTypes, runCount, uniqueName } from './helpers'

// A host of this file's own, where a trigger more than 3 seconds late misfires (instead of a minute late), and
// misfires raise alerts, posted and emailed as failures are
test.use({
  hostSettings: {
    'Quartz__quartz.jobStore.misfireThreshold': '3000',
    CronlightAlerts__NotifyOnMisfire: 'true',
  },
})

test('a trigger resumed after missing its fire times raises a misfire alert, posted and emailed', async ({
  request,
  sampleHost,
}) => {
  const { webhooks, mail } = sampleHost
  const name = uniqueName('misfiring')
  await createJob(request, { type: jobTypes.plain, name, triggers: [{ cronExpression: '* * * * * ?' }] })
  try {
    // Paused past the threshold, so its fire times are missed; resuming it catches up
    expect((await request.post(`api/jobs/DEFAULT/${name}/pause`)).status()).toBe(204)
    await new Promise(resolve => setTimeout(resolve, 5_000))
    expect((await request.post(`api/jobs/DEFAULT/${name}/resume`)).status()).toBe(204)

    await expect.poll(() => webhooks.alertsFor(name).length, { timeout: 20_000 }).toBeGreaterThanOrEqual(1)
    const [alert] = webhooks.alertsFor(name)
    expect(alert).toMatchObject({
      kind: 'Misfired',
      jobGroup: 'DEFAULT',
      jobName: name,
      jobType: jobTypes.plain,
      consecutiveFailures: 0,
    })
    expect(alert.error).toMatch(/^Missed \d+ fire times, the first by \d+s; Quartz runs it once now/)
    // History writes in the background
    await expect.poll(() => runCount(request, 'DEFAULT', name, 'Misfired')).toBeGreaterThanOrEqual(1)

    await expect.poll(() => mail.emailsFor(name).length, { timeout: 20_000 }).toBeGreaterThanOrEqual(1)
    const [email] = mail.emailsFor(name)
    expect(email.subject).toBe(`[Cronlight] Job DEFAULT.${name} missed its fire time`)
    expect(email.body).toContain(alert.error as string)
  } finally {
    // It fires every second: not left running for the file's other tests
    await request.delete(`api/jobs/DEFAULT/${name}`)
  }
})
