import type { ExecutionRecord, Job, SchedulerInfo, Trigger } from '@/types'

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
  ...overrides,
})
