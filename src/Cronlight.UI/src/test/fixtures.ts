import { jobStatus, typeLabel } from '@/lib/format'
import type { ExecutionRecord, Job, JobPage, SchedulerInfo, Trigger } from '@/types'

export const trigger = (overrides: Partial<Trigger> = {}): Trigger => ({
  name: 't1',
  group: 'DEFAULT',
  kind: 'Cron',
  cronExpression: '0 0 6 * * ?',
  timeZone: 'UTC',
  state: 'Normal',
  nextFireTimeUtc: '2030-01-01T06:00:00Z',
  previousFireTimeUtc: null,
  ...overrides,
})

export const job = (overrides: Partial<Job> = {}): Job => ({
  name: 'nightly',
  group: 'DEFAULT',
  description: null,
  jobType: 'Acme.Jobs.NightlyJob',
  displayName: null,
  durable: true,
  loadable: true,
  loadError: null,
  data: {},
  triggers: [trigger()],
  retry: null,
  ...overrides,
})

export const scheduler = (overrides: Partial<SchedulerInfo> = {}): SchedulerInfo => ({
  schedulerName: 'SampleHost',
  schedulerInstanceId: 'node-1',
  version: '3.13.1',
  started: true,
  inStandbyMode: false,
  jobStore: 'RAMJobStore',
  jobStoreClustered: false,
  jobStoreSupportsPersistence: false,
  threadPoolSize: 10,
  numberOfJobsExecuted: 0,
  runningSince: null,
  canManage: true,
  historyEnabled: true,
  showScheduleAssistant: true,
  secretReferences: false,
  ...overrides,
})

export const execution = (
  id: number,
  overrides: Partial<ExecutionRecord> = {}
): ExecutionRecord => ({
  id,
  schedulerName: 'test',
  fireInstanceId: `fire-${id}`,
  instanceName: 'node-1',
  jobName: 'nightly',
  jobGroup: 'DEFAULT',
  jobType: null,
  triggerName: null,
  triggerGroup: null,
  scheduledFireTimeUtc: null,
  firedTimeUtc: '2030-01-01T00:00:00Z',
  finishedTimeUtc: null,
  durationMs: null,
  status: 'Succeeded',
  error: null,
  refireCount: 0,
  attempt: 1,
  ...overrides,
})

/** Answers a jobs/search path the way the server does, over the given jobs. */
export function jobPage(jobs: Job[], path = 'jobs/search?'): JobPage {
  const params = new URLSearchParams(path.split('?')[1])
  const search = (params.get('search') ?? '').toLowerCase()
  const type = params.get('type')
  const status = params.get('status')
  const page = Number(params.get('page') ?? 1)
  const pageSize = Number(params.get('pageSize') ?? 50)
  const matching = jobs.filter(
    j =>
      (!search ||
        j.name.toLowerCase().includes(search) ||
        j.group.toLowerCase().includes(search)) &&
      (!type || typeLabel(j) === type) &&
      (!status || jobStatus(j) === status)
  )
  const count = (s: string) => jobs.filter(j => jobStatus(j) === s).length
  return {
    jobs: matching.slice((page - 1) * pageSize, page * pageSize),
    total: matching.length,
    page,
    pageSize,
    counts: {
      all: jobs.length,
      active: count('Active'),
      paused: count('Paused'),
      unloadable: count('Unloadable'),
    },
    types: [...new Set(jobs.map(typeLabel))].sort(),
  }
}
