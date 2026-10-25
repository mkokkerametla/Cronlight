import { expect, type APIRequestContext, type Page } from '@playwright/test'

export const jobTypes = {
  invoice: 'Cronlight.SampleHost.InvoiceJob',
  plain: 'Cronlight.SampleHost.PlainJob',
  flaky: 'Cronlight.SampleHost.FlakyJob',
  http: 'Cronlight.SampleHost.HttpJob',
  slow: 'Cronlight.SampleHost.SlowJob',
} as const

/** Job names are unique per test, since all tests share one scheduler. */
export const uniqueName = (prefix: string) => `${prefix}-${Date.now().toString(36)}`

interface CreateJob {
  type: string
  name: string
  group?: string
  data?: Record<string, string>
  triggers?: { name?: string; cronExpression: string; timeZone?: string }[]
}

/** Arranges a job through the API, for tests whose subject is something else. */
export async function createJob(request: APIRequestContext, job: CreateJob) {
  const response = await request.post('api/jobs', { data: job })
  expect(response.status(), await response.text()).toBe(201)
}

export async function openDashboard(page: Page) {
  await page.goto('./')
  await expect(page.getByRole('tab', { name: /Jobs/ })).toBeVisible()
}

/** The jobs table row for a job, found by its name. */
export const jobRow = (page: Page, name: string) =>
  page.getByRole('row').filter({ has: page.getByText(name, { exact: true }) })

/** Picks an option from a shadcn/Radix select. */
export async function choose(page: Page, trigger: ReturnType<Page['locator']>, option: string | RegExp) {
  await trigger.click()
  await page.getByRole('option', { name: option }).click()
}

/** Starts a job once through the API. */
export async function triggerJob(request: APIRequestContext, group: string, name: string) {
  const response = await request.post(`api/jobs/${group}/${name}/trigger`)
  expect(response.ok(), await response.text()).toBe(true)
}

/** A stat card, by its label. Its accessible name is the count followed by the label. */
export const statCard = (page: Page, label: string) =>
  page.getByRole('button', { name: new RegExp(`^\\d+\\s*${escapeRegExp(label)}`) })

const escapeRegExp = (text: string) => text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')

/** Number of recorded runs of a job, optionally only those with one outcome, read through the API. */
export async function runCount(request: APIRequestContext, group: string, name: string, status?: string) {
  const query = new URLSearchParams({ group, name, limit: '500', ...(status && { status }) })
  const response = await request.get(`api/history?${query}`)
  return ((await response.json()) as { records: unknown[] }).records.length
}
