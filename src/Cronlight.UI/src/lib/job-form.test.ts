import { describe, expect, it } from 'vitest'
import {
  dataChanges,
  dataRows,
  draftsFromTriggers,
  draftsToRequest,
  emptyTrigger,
  MASK,
  rowsToData,
} from '@/lib/job-form'
import { trigger } from '@/test/fixtures'
import type { JobParameter, JobType } from '@/types'

const param = (key: string, overrides: Partial<JobParameter> = {}): JobParameter => ({
  key,
  type: 'String',
  required: false,
  description: null,
  defaultValue: null,
  choices: null,
  ...overrides,
})

const jobType = (parameters: JobParameter[]): JobType =>
  ({ typeName: 'Acme.ReportJob', displayName: 'Report', parameters }) as JobType

describe('dataRows', () => {
  it('lists declared parameters first, in order, with stored values or defaults', () => {
    const type = jobType([param('format', { defaultValue: 'pdf' }), param('pages')])

    const rows = dataRows(type, { pages: '10', extra: 'x' })

    expect(rows.map(r => [r.key, r.value, !!r.param])).toEqual([
      ['format', 'pdf', true],
      ['pages', '10', true],
      ['extra', 'x', false],
    ])
  })

  it('turns null values into empty strings and works without a job type', () => {
    expect(dataRows(undefined, { a: null })).toEqual([{ key: 'a', value: '' }])
  })
})

describe('rowsToData', () => {
  it('trims keys and drops rows without a key', () => {
    expect(
      rowsToData([
        { key: '  region ', value: 'eu' },
        { key: '  ', value: 'lost' },
      ])
    ).toEqual({
      region: 'eu',
    })
  })

  it('drops empty free-form values but keeps empty declared parameters', () => {
    const rows = [
      { key: 'note', value: '' },
      { key: 'customerId', value: '', param: param('customerId', { required: true }) },
    ]
    expect(rowsToData(rows)).toEqual({ customerId: '' })
  })

  it('sends a masked secret back unchanged, so the server keeps the stored value', () => {
    expect(rowsToData([{ key: 'apiKey', value: MASK }])).toEqual({ apiKey: MASK })
  })
})

describe('dataChanges', () => {
  const saved = { region: 'eu', cursor: '400', constructor: 'x' }
  const opened = dataRows(undefined, saved)

  it('sends entries changed or added, and removed ones as null, but none left as they were', () => {
    const rows = [
      { key: 'region', value: 'us' },
      { key: 'cursor', value: '400' },
      { key: 'batch', value: '10' },
    ]
    expect(dataChanges(rows, opened, saved)).toEqual({
      region: 'us',
      batch: '10',
      constructor: null,
    })
  })

  it('takes keys named like object properties as plain keys', () => {
    expect(dataChanges([{ key: 'toString', value: 'y' }], [], {})).toEqual({ toString: 'y' })
    expect(dataChanges(opened, opened, saved)).toEqual({})
  })

  it('sends a parameter default the saved data does not hold, so a required one is not missing', () => {
    const type = jobType([param('batch', { required: true, defaultValue: '50' })])
    const rows = dataRows(type, { region: 'eu' })

    expect(dataChanges(rows, rows, { region: 'eu' })).toEqual({ batch: '50' })
  })
})

describe('trigger drafts', () => {
  it('edits cron, one-time and interval triggers, shows UTC as blank and gives each row its own id', () => {
    const drafts = draftsFromTriggers([
      trigger({ name: 'a', timeZone: 'UTC' }),
      trigger({ name: 'b', timeZone: 'Europe/London' }),
      trigger({
        name: 'once',
        kind: 'Once',
        cronExpression: null,
        timeZone: null,
        startTimeUtc: '2030-01-01T06:00:00Z',
      }),
      trigger({ name: 'retry', kind: 'Retry', cronExpression: null, timeZone: null }),
      trigger({ name: 'daily', kind: 'DailyTimeIntervalTriggerImpl', cronExpression: null }),
    ])

    expect(drafts.map(d => [d.name, d.kind, d.timeZone])).toEqual([
      ['a', 'Cron', ''],
      ['b', 'Cron', 'Europe/London'],
      ['once', 'Once', ''],
    ])
    expect(new Set(drafts.map(d => d.id)).size).toBe(3)
  })

  it('starts with one empty cron row when there are no triggers it edits', () => {
    const [only, ...rest] = draftsFromTriggers([])
    expect(rest).toHaveLength(0)
    expect(only).toMatchObject({ kind: 'Cron', name: '', cronExpression: '', timeZone: '' })
  })

  it('gives every new row a fresh id', () => {
    expect(emptyTrigger().id).not.toBe(emptyTrigger().id)
  })

  it('builds the request from rows with a cron expression, trimmed, without client-only ids', () => {
    const request = draftsToRequest([
      {
        ...emptyTrigger(),
        name: ' nightly ',
        cronExpression: ' 0 0 2 * * ? ',
        timeZone: ' Europe/London ',
        skipMissed: true,
      },
      { ...emptyTrigger(), cronExpression: '0 0 3 * * ?' },
      { ...emptyTrigger(), name: 'blank', cronExpression: '   ' },
    ])

    expect(request).toEqual([
      {
        name: 'nightly',
        cronExpression: '0 0 2 * * ?',
        timeZone: 'Europe/London',
        skipMissed: true,
      },
      { name: null, cronExpression: '0 0 3 * * ?', timeZone: null },
    ])
  })

  it('sends one-time and interval rows with their times in UTC, leaving out unfinished ones', () => {
    const request = draftsToRequest([
      { ...emptyTrigger('Once'), name: 'launch', startAt: '2030-03-01T09:30' },
      { ...emptyTrigger('Once'), name: 'no time' },
      { ...emptyTrigger('Interval'), every: '15', unit: 'minutes' },
      {
        ...emptyTrigger('Interval'),
        every: '2',
        unit: 'hours',
        startAt: '2030-03-01T08:00',
        runs: '3',
        skipMissed: true,
      },
      { ...emptyTrigger('Interval'), name: 'no interval', every: '0' },
    ])

    expect(request).toEqual([
      { name: 'launch', kind: 'Once', startAtUtc: new Date('2030-03-01T09:30').toISOString() },
      { name: null, kind: 'Interval', intervalSeconds: 900 },
      {
        name: null,
        kind: 'Interval',
        intervalSeconds: 7200,
        startAtUtc: new Date('2030-03-01T08:00').toISOString(),
        // Three runs in all: the first and two repeats
        repeatCount: 2,
        skipMissed: true,
      },
    ])
  })

  it('reads an interval in its largest whole unit and its repeats as runs in all', () => {
    const [every] = draftsFromTriggers([
      trigger({
        kind: 'Interval',
        cronExpression: null,
        startTimeUtc: '2030-01-01T06:00:00Z',
        intervalSeconds: 7200,
        repeatCount: 4,
      }),
    ])
    expect(every).toMatchObject({ every: '2', unit: 'hours', runs: '5' })
  })

  it('sends back the stored start time while it is unchanged, so the trigger is left alone', () => {
    const stored = '2030-01-01T06:00:12.345Z'
    const [draft] = draftsFromTriggers([
      trigger({
        kind: 'Interval',
        cronExpression: null,
        startTimeUtc: stored,
        intervalSeconds: 60,
      }),
    ])
    expect(draftsToRequest([draft])[0].startAtUtc).toBe(stored)
    expect(draftsToRequest([{ ...draft, startAt: '2030-02-01T06:00' }])[0].startAtUtc).toBe(
      new Date('2030-02-01T06:00').toISOString()
    )
  })

  it('reads whether a trigger skips missed runs from its misfire instruction', () => {
    const drafts = draftsFromTriggers([
      trigger({ name: 'skips', misfireInstruction: 'DoNothing' }),
      trigger({ name: 'runs', misfireInstruction: 'SmartPolicy' }),
    ])
    expect(drafts.map(d => d.skipMissed)).toEqual([true, false])
  })
})
