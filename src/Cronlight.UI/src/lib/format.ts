import type { Job, JobRetryPolicy, JobStatus } from '@/types'

export type { JobStatus }

export function formatDuration(ms: number | null | undefined): string {
  if (ms === null || ms === undefined) return '-'
  if (ms < 1000) return `${ms}ms`
  if (ms < 60_000) return `${(ms / 1000).toFixed(1)}s`
  if (ms < 3_600_000) return `${Math.floor(ms / 60_000)}m ${Math.round((ms % 60_000) / 1000)}s`
  return `${(ms / 3_600_000).toFixed(1)}h`
}

const dateTime = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'medium' })
const relative = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto', style: 'short' })

export const formatDateTime = (iso: string | null | undefined) =>
  iso ? dateTime.format(new Date(iso)) : '-'

export function formatRelative(iso: string | null | undefined): string {
  if (!iso) return '-'
  const seconds = (new Date(iso).getTime() - Date.now()) / 1000
  const abs = Math.abs(seconds)
  if (abs < 45) return seconds < 0 ? 'just now' : 'now'
  if (abs < 3600) return relative.format(Math.round(seconds / 60), 'minute')
  if (abs < 86_400) return relative.format(Math.round(seconds / 3600), 'hour')
  return relative.format(Math.round(seconds / 86_400), 'day')
}

export const shortType = (type: string | null | undefined) => type?.split('.').pop() ?? '-'

export const jobKey = (job: Pick<Job, 'group' | 'name'>) => `${job.group}.${job.name}`

/** A job is paused when it has triggers and every one of them is paused. */
export const isPaused = (job: Job) =>
  job.triggers.length > 0 && job.triggers.every(t => t.state === 'Paused')

export const jobStatus = (job: Job): JobStatus =>
  !job.loadable ? 'Unloadable' : isPaused(job) ? 'Paused' : 'Active'

export const typeLabel = (job: Job) =>
  job.loadable ? (job.displayName ?? shortType(job.jobType)) : 'Unloadable'

export const nextFire = (job: Job) =>
  job.triggers
    .map(t => t.nextFireTimeUtc)
    .filter((t): t is string => t !== null)
    .sort()[0] ?? null

/** A wait in whole seconds: 45s, 2m, 1h 30m */
export function formatSeconds(seconds: number): string {
  if (seconds < 60) return `${seconds}s`
  const h = Math.floor(seconds / 3600)
  const m = Math.floor((seconds % 3600) / 60)
  const s = seconds % 60
  if (h) return m ? `${h}h ${m}m` : `${h}h`
  return s ? `${m}m ${s}s` : `${m}m`
}

/** "every 10 seconds", or "every second" */
export const everySeconds = (seconds: number) =>
  seconds === 1 ? 'every second' : `every ${seconds} seconds`

/** The waits before each retry, in seconds, never longer than a day (as the server caps them) */
export const retryDelays = (policy: JobRetryPolicy): number[] =>
  Array.from({ length: Math.max(0, policy.maxRetries) }, (_, i) =>
    policy.exponential ? Math.min(policy.delaySeconds * 2 ** i, 86_400) : policy.delaySeconds
  )

/** "Up to 3 retries, 30s apart", or with doubling "Up to 3 retries, after 30s, 1m, 2m" */
export function describeRetry(policy: JobRetryPolicy | null | undefined): string {
  if (!policy || policy.maxRetries <= 0) return 'None'
  if (policy.maxRetries === 1) return `1 retry, after ${formatSeconds(policy.delaySeconds)}`
  const text = `Up to ${policy.maxRetries} retries`
  if (!policy.exponential) return `${text}, ${formatSeconds(policy.delaySeconds)} apart`
  return `${text}, after ${retryDelays(policy).map(formatSeconds).join(', ')}`
}
