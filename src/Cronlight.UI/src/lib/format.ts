import type { ExecutionRecord, Job, JobRetryPolicy, JobStatus, Trigger } from '@/types'

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

/** What Quartz does when a trigger misses a fire time, from its misfire instruction */
export function describeMisfire(instruction: string | undefined): string | null {
  switch (instruction) {
    case undefined:
      return null
    case 'SmartPolicy':
    case 'FireOnceNow':
    case 'FireNow':
      return 'Runs once as soon as it can'
    case 'DoNothing':
    case 'RescheduleNextWithRemainingCount':
    case 'RescheduleNextWithExistingCount':
      return 'Skipped; waits for the next fire time'
    case 'RescheduleNowWithExistingRepeatCount':
    case 'RescheduleNowWithRemainingRepeatCount':
      return 'Runs as soon as it can, and later fire times move with it'
    case 'IgnoreMisfirePolicy':
      return 'Runs every missed fire time'
    default:
      return instruction
  }
}

/** A run that started this much after its scheduled time is marked as late */
export const lateAfterMs = 5000

/** How late a run started, when late enough to mention ("2m 5s"), else null. Misfires say so themselves. */
export function startedLate(r: ExecutionRecord): string | null {
  if (r.status === 'Misfired' || !r.scheduledFireTimeUtc) return null
  const ms = new Date(r.firedTimeUtc).getTime() - new Date(r.scheduledFireTimeUtc).getTime()
  return ms >= lateAfterMs ? formatSeconds(Math.round(ms / 1000)) : null
}

/** A misfire is shown at the fire time it missed, a run at the time it fired */
export const recordTime = (r: ExecutionRecord) =>
  r.status === 'Misfired' ? (r.scheduledFireTimeUtc ?? r.firedTimeUtc) : r.firedTimeUtc

/** A misfire's error says what Quartz did, which is not a failure */
export const errorTone = (r: ExecutionRecord) =>
  r.status === 'Misfired' ? 'text-muted-foreground' : 'text-destructive'

export const shortType = (type: string | null | undefined) => type?.split('.').pop() ?? '-'

export const jobKey = (job: Pick<Job, 'group' | 'name'>) => `${job.group}.${job.name}`

/** A job is paused when it has triggers and every one of them is paused. */
export const isPaused = (job: Job) =>
  job.triggers.length > 0 && job.triggers.every(t => t.state === 'Paused')

/**
 * Quartz has stopped the job: a trigger is in its Error state, as every trigger of a job Quartz could not create is. It
 * does not run again until it is resumed.
 */
export const isStopped = (job: Job) => job.triggers.some(t => t.state === 'Error')

/** Resuming the job would start it again: it is paused, or stopped by Quartz. */
export const canResume = (job: Job) => isPaused(job) || isStopped(job)

export const jobStatus = (job: Job): JobStatus =>
  !job.loadable ? 'Unloadable' : isStopped(job) ? 'Error' : isPaused(job) ? 'Paused' : 'Active'

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

/** What a one-time or interval trigger does, e.g. "Every 5m, 10 runs in all"; null for other kinds */
export function describeSchedule(t: Trigger): string | null {
  if (t.kind === 'Once') return `Once, ${formatDateTime(t.startTimeUtc)}`
  if (t.kind !== 'Interval' || !t.intervalSeconds) return null
  const runs = t.repeatCount != null ? `, ${t.repeatCount + 1} runs in all` : ''
  return `Every ${formatSeconds(t.intervalSeconds)} from ${formatDateTime(t.startTimeUtc)}${runs}`
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
  const only = policy.retryOn?.length ? `, only on ${policy.retryOn.join(', ')}` : ''
  if (policy.maxRetries === 1) return `1 retry, after ${formatSeconds(policy.delaySeconds)}${only}`
  const text = `Up to ${policy.maxRetries} retries`
  if (!policy.exponential) return `${text}, ${formatSeconds(policy.delaySeconds)} apart${only}`
  return `${text}, after ${retryDelays(policy).map(formatSeconds).join(', ')}${only}`
}
