import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import {
  describeMisfire,
  describeRetry,
  startedLate,
  formatSeconds,
  formatDuration,
  formatRelative,
  isPaused,
  jobStatus,
  nextFire,
  shortType,
  typeLabel,
} from '@/lib/format'
import { execution, job, trigger } from '@/test/fixtures'

describe('formatDuration', () => {
  it.each([
    [null, '-'],
    [undefined, '-'],
    [0, '0ms'],
    [999, '999ms'],
    [1500, '1.5s'],
    [59_999, '60.0s'],
    [61_000, '1m 1s'],
    [3_599_000, '59m 59s'],
    [5_400_000, '1.5h'],
  ])('%s ms is %s', (ms, expected) => {
    expect(formatDuration(ms)).toBe(expected)
  })
})

describe('formatRelative', () => {
  const now = new Date('2030-01-01T12:00:00Z')
  const relative = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto', style: 'short' })
  const at = (seconds: number) => new Date(now.getTime() + seconds * 1000).toISOString()

  beforeEach(() => {
    vi.useFakeTimers()
    vi.setSystemTime(now)
  })
  afterEach(() => {
    vi.useRealTimers()
  })

  it('says now or just now within 45 seconds', () => {
    expect(formatRelative(at(30))).toBe('now')
    expect(formatRelative(at(-30))).toBe('just now')
  })

  it('picks minutes, hours or days by distance', () => {
    expect(formatRelative(at(5 * 60))).toBe(relative.format(5, 'minute'))
    expect(formatRelative(at(-3 * 3600))).toBe(relative.format(-3, 'hour'))
    expect(formatRelative(at(2 * 86_400))).toBe(relative.format(2, 'day'))
  })

  it('shows a dash when there is no time', () => {
    expect(formatRelative(null)).toBe('-')
  })
})

describe('job status helpers', () => {
  it('treats a job as paused only when it has triggers and all of them are paused', () => {
    expect(isPaused(job({ triggers: [trigger({ state: 'Paused' })] }))).toBe(true)
    expect(
      isPaused(
        job({ triggers: [trigger({ state: 'Paused' }), trigger({ name: 't2', state: 'Normal' })] })
      )
    ).toBe(false)
    expect(isPaused(job({ triggers: [] }))).toBe(false)
  })

  it('reports unloadable before paused', () => {
    expect(jobStatus(job({ loadable: false, triggers: [trigger({ state: 'Paused' })] }))).toBe(
      'Unloadable'
    )
    expect(jobStatus(job({ triggers: [trigger({ state: 'Paused' })] }))).toBe('Paused')
    expect(jobStatus(job())).toBe('Active')
  })

  it('labels a job by display name, then short type name', () => {
    expect(typeLabel(job({ displayName: 'Nightly report' }))).toBe('Nightly report')
    expect(typeLabel(job())).toBe('NightlyJob')
    expect(typeLabel(job({ loadable: false }))).toBe('Unloadable')
    expect(shortType(null)).toBe('-')
  })

  it('finds the earliest next fire time across triggers', () => {
    const j = job({
      triggers: [
        trigger({ nextFireTimeUtc: '2030-03-01T00:00:00Z' }),
        trigger({ name: 't2', nextFireTimeUtc: null }),
        trigger({ name: 't3', nextFireTimeUtc: '2030-02-01T00:00:00Z' }),
      ],
    })
    expect(nextFire(j)).toBe('2030-02-01T00:00:00Z')
    expect(nextFire(job({ triggers: [] }))).toBeNull()
  })

  it('describes retry policies', () => {
    expect(describeRetry(null)).toBe('None')
    expect(describeRetry({ maxRetries: 0, delaySeconds: 60, exponential: true })).toBe('None')
    expect(describeRetry({ maxRetries: 1, delaySeconds: 45, exponential: true })).toBe(
      '1 retry, after 45s'
    )
    expect(describeRetry({ maxRetries: 3, delaySeconds: 600, exponential: false })).toBe(
      'Up to 3 retries, 10m apart'
    )
    // Doubling stops at a day, as on the server
    expect(describeRetry({ maxRetries: 3, delaySeconds: 30_000, exponential: true })).toBe(
      'Up to 3 retries, after 8h 20m, 16h 40m, 24h'
    )
  })

  it('formats waits in seconds', () => {
    expect(formatSeconds(59)).toBe('59s')
    expect(formatSeconds(90)).toBe('1m 30s')
    expect(formatSeconds(3600)).toBe('1h')
    expect(formatSeconds(5400)).toBe('1h 30m')
  })
})

describe('misfires and late starts', () => {
  it('describes what a misfire instruction does', () => {
    expect(describeMisfire('SmartPolicy')).toBe('Runs once as soon as it can')
    expect(describeMisfire('DoNothing')).toBe('Skipped; waits for the next fire time')
    expect(describeMisfire('IgnoreMisfirePolicy')).toBe('Runs every missed fire time')
    expect(describeMisfire(undefined)).toBeNull()
  })

  it('marks a run that started 5 seconds or more after its scheduled time', () => {
    const at = (s: number) => new Date(Date.UTC(2030, 0, 1, 0, 0, s)).toISOString()
    expect(
      startedLate(execution(1, { scheduledFireTimeUtc: at(0), firedTimeUtc: at(4) }))
    ).toBeNull()
    expect(startedLate(execution(1, { scheduledFireTimeUtc: at(0), firedTimeUtc: at(30) }))).toBe(
      '30s'
    )
    // A misfire says so itself
    expect(
      startedLate(
        execution(1, { status: 'Misfired', scheduledFireTimeUtc: at(0), firedTimeUtc: at(30) })
      )
    ).toBeNull()
  })

  it('names the exception types a retry is limited to', () => {
    expect(
      describeRetry({
        maxRetries: 1,
        delaySeconds: 30,
        exponential: false,
        retryOn: ['TimeoutException'],
      })
    ).toBe('1 retry, after 30s, only on TimeoutException')
  })
})
