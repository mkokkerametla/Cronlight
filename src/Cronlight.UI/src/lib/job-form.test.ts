import { describe, expect, it } from 'vitest'
import {
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

describe('trigger drafts', () => {
  it('edits only cron triggers, shows UTC as blank and gives each row its own id', () => {
    const drafts = draftsFromTriggers([
      trigger({ name: 'a', timeZone: 'UTC' }),
      trigger({ name: 'b', timeZone: 'Europe/London' }),
      trigger({ name: 'once', kind: 'Simple', cronExpression: null }),
    ])

    expect(drafts.map(d => [d.name, d.timeZone])).toEqual([
      ['a', ''],
      ['b', 'Europe/London'],
    ])
    expect(new Set(drafts.map(d => d.id)).size).toBe(2)
  })

  it('starts with one empty row when there are no cron triggers', () => {
    const [only, ...rest] = draftsFromTriggers([])
    expect(rest).toHaveLength(0)
    expect(only).toMatchObject({ name: '', cronExpression: '', timeZone: '' })
  })

  it('gives every new row a fresh id', () => {
    expect(emptyTrigger().id).not.toBe(emptyTrigger().id)
  })

  it('builds the request from rows with a cron expression, trimmed, without client-only ids', () => {
    const request = draftsToRequest([
      { id: 1, name: ' nightly ', cronExpression: ' 0 0 2 * * ? ', timeZone: ' Europe/London ' },
      { id: 2, name: '', cronExpression: '0 0 3 * * ?', timeZone: '' },
      { id: 3, name: 'blank', cronExpression: '   ', timeZone: '' },
    ])

    expect(request).toEqual([
      { name: 'nightly', cronExpression: '0 0 2 * * ?', timeZone: 'Europe/London' },
      { name: null, cronExpression: '0 0 3 * * ?', timeZone: null },
    ])
  })
})
