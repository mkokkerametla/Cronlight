import type { APIRequestContext } from '@playwright/test'
import { expect, test } from './fixtures'
import { createJob, jobTypes, runCount, triggerJob, uniqueName } from './helpers'

// The sample alerts after two failures in a row and on recovery; each worker's host posts its alerts to a JSON, a
// Slack and a Teams webhook on the worker's receiver, and emails them to its SMTP receiver (fixtures.ts). Alerts are sent one at a time, in the order
// the runs finished, so once an alert has arrived, any the job's earlier runs raised have arrived too.

/** Runs the job once and waits until history has its outcome, which the next run's streak follows on from. */
async function runAndWait(request: APIRequestContext, name: string, status: string, count: number) {
  await triggerJob(request, 'DEFAULT', name)
  await expect.poll(() => runCount(request, 'DEFAULT', name, status), { timeout: 20_000 }).toBe(count)
}

async function setFailureRate(request: APIRequestContext, name: string, rate: string) {
  const response = await request.put(`api/jobs/DEFAULT/${name}`, { data: { data: { failureRate: rate } } })
  expect(response.status(), await response.text()).toBe(204)
}

test('a job that fails twice in a row alerts each webhook and by email once, and again when it recovers', async ({
  request,
  sampleHost,
}) => {
  const { webhooks, mail, baseURL } = sampleHost
  const name = uniqueName('alerting')
  await createJob(request, { type: jobTypes.flaky, name, data: { failureRate: '100' } })

  await runAndWait(request, name, 'Failed', 1)
  await runAndWait(request, name, 'Failed', 2)
  await expect.poll(() => webhooks.alertsFor(name).length, { timeout: 20_000 }).toBe(1)
  // A third failure in the same streak raises no other alert
  await runAndWait(request, name, 'Failed', 3)
  await setFailureRate(request, name, '0')
  await runAndWait(request, name, 'Succeeded', 1)
  await expect.poll(() => webhooks.alertsFor(name).length, { timeout: 20_000 }).toBe(2)

  const [failing, recovered] = webhooks.alertsFor(name)
  expect(failing).toMatchObject({
    kind: 'Failing',
    schedulerName: 'SampleHost',
    jobGroup: 'DEFAULT',
    jobName: name,
    jobType: jobTypes.flaky,
    consecutiveFailures: 2,
    error: 'HttpRequestException: Upstream API returned 503 Service Unavailable',
    dashboardUrl: baseURL,
  })
  expect(recovered).toMatchObject({ kind: 'Recovered', jobName: name, consecutiveFailures: 3, error: null })

  // Each webhook is posted to in turn, the JSON one first
  await expect.poll(() => webhooks.chatPostsFor(name, '/teams').length, { timeout: 20_000 }).toBe(2)
  const slack = webhooks.chatPostsFor(name, '/slack').map(post => post.text as string)
  expect(slack).toHaveLength(2)
  expect(slack[0]).toContain(`:x: *Job DEFAULT.${name} failed 2 times in a row*`)
  expect(slack[0]).toContain('```HttpRequestException: Upstream API returned 503 Service Unavailable```')
  expect(slack[0]).toContain(`<${baseURL}|Open Cronlight>`)
  expect(slack[1]).toContain(`:white_check_mark: *Job DEFAULT.${name} succeeded again after 3 failures in a row*`)

  type Card = { body: { text?: string; color?: string }[]; actions: { url: string }[] }
  const cards = webhooks
    .chatPostsFor(name, '/teams')
    .map(post => (post as { attachments: { content: Card }[] }).attachments[0].content)
  expect(cards[0].body[0]).toMatchObject({ text: `Job DEFAULT.${name} failed 2 times in a row`, color: 'Attention' })
  expect(cards[0].actions[0].url).toBe(baseURL)
  expect(cards[1].body[0]).toMatchObject({ text: `Job DEFAULT.${name} succeeded again after 3 failures in a row`, color: 'Good' })

  // And by email, in plain text
  await expect.poll(() => mail.emailsFor(name).length, { timeout: 20_000 }).toBe(2)
  const [failingEmail, recoveredEmail] = mail.emailsFor(name)
  expect(failingEmail.to).toEqual(['oncall@example.com'])
  expect(failingEmail.subject).toBe(`[Cronlight] Job DEFAULT.${name} failed 2 times in a row`)
  expect(failingEmail.body).toContain(`Job:       DEFAULT.${name}\nType:      ${jobTypes.flaky}\n`)
  expect(failingEmail.body).toContain('HttpRequestException: Upstream API returned 503 Service Unavailable')
  expect(failingEmail.body).toContain(`Open Cronlight: ${baseURL}`)
  expect(recoveredEmail.subject).toBe(`[Cronlight] Job DEFAULT.${name} succeeded again after 3 failures in a row`)
})

test('only the last attempt of a retried run counts towards an alert', async ({ request, sampleHost }) => {
  const { webhooks } = sampleHost
  const name = uniqueName('alerting-retried')
  await createJob(request, {
    type: jobTypes.flaky,
    name,
    data: { failureRate: '100' },
    retry: { maxRetries: 1, delaySeconds: 1 },
  })

  // Two runs of two attempts each: two failures in a row, not four
  await runAndWait(request, name, 'Failed', 1)
  expect(await runCount(request, 'DEFAULT', name, 'Retried')).toBe(1)
  await runAndWait(request, name, 'Failed', 2)
  await expect.poll(() => webhooks.alertsFor(name).length, { timeout: 20_000 }).toBe(1)
  await setFailureRate(request, name, '0')
  await runAndWait(request, name, 'Succeeded', 1)
  await expect.poll(() => webhooks.alertsFor(name).length, { timeout: 20_000 }).toBe(2)

  // Counting the retried attempts would have alerted after the first run, and recovered after 4 failures
  expect(webhooks.alertsFor(name).map(a => [a.kind, a.consecutiveFailures])).toEqual([
    ['Failing', 2],
    ['Recovered', 2],
  ])
})
