// Mirrors the DTOs in src/Cronlight/DashboardApi.cs and RunningJobsReader.cs

export type TriggerState = 'None' | 'Normal' | 'Paused' | 'Complete' | 'Error' | 'Blocked'

export interface Trigger {
  name: string
  group: string
  kind: 'Cron' | 'Simple' | string
  cronExpression: string | null
  timeZone: string | null
  state: TriggerState
  nextFireTimeUtc: string | null
  previousFireTimeUtc: string | null
}

/** How often, and how long apart, a failed run is retried */
export interface JobRetryPolicy {
  /** Retries after the first attempt; 0 means none */
  maxRetries: number
  /** Wait before the first retry */
  delaySeconds: number
  /** Double the wait before each further retry */
  exponential: boolean
}

export interface Job {
  name: string
  group: string
  description: string | null
  jobType: string | null
  displayName: string | null
  durable: boolean
  loadable: boolean
  loadError: string | null
  data: Record<string, string | null>
  triggers: Trigger[]
  /** The policy the job runs with, its own or its type's; null when failed runs are not retried */
  retry: JobRetryPolicy | null
}

export type JobParameterType = 'String' | 'Int' | 'Bool' | 'Url' | 'Text' | 'Secret'

export interface JobParameter {
  key: string
  type: JobParameterType
  required: boolean
  description: string | null
  defaultValue: string | null
  choices: string[] | null
}

export interface JobType {
  typeName: string
  displayName: string
  description: string | null
  hidden: boolean
  parameters: JobParameter[]
  /** From [CronlightRetry]: what new jobs of this type start with */
  retry: JobRetryPolicy | null
}

export interface SchedulerInfo {
  schedulerName: string
  schedulerInstanceId: string
  version: string
  started: boolean
  inStandbyMode: boolean
  jobStore: string
  jobStoreClustered: boolean
  jobStoreSupportsPersistence: boolean
  threadPoolSize: number
  numberOfJobsExecuted: number
  runningSince: string | null
  canManage: boolean
  historyEnabled: boolean
  showScheduleAssistant: boolean
  secretReferences: boolean
  /** How often the dashboard refreshes, in seconds */
  refreshSeconds: number
  /** Every scheduler the dashboard serves, the host's own first */
  schedulers: string[]
}

/** Counts over every job, whatever the search and filters */
export interface JobCounts {
  all: number
  active: number
  paused: number
  unloadable: number
}

/** One page of the job list, from jobs/search */
export interface JobPage {
  jobs: Job[]
  /** Jobs matching the search and filters, on every page */
  total: number
  page: number
  pageSize: number
  counts: JobCounts
  /** Every job type label in the scheduler, for the type filter */
  types: string[]
}

export type JobStatus = 'Active' | 'Paused' | 'Unloadable'

export interface JobQuery {
  search: string
  type: string | null
  status: JobStatus | null
  page: number
}

export interface RunningJob {
  name: string
  group: string
  jobType: string | null
  triggerName: string
  triggerGroup: string
  fireInstanceId: string
  instanceName: string
  firedTimeUtc: string
  runningForMs: number
}

export interface RunningJobs {
  source: 'cluster' | 'local'
  jobs: RunningJob[]
}

export interface CronPreview {
  isValid: boolean
  expression: string
  description: string | null
  nextFireTimes: string[]
  errorMessage: string | null
}

export interface TriggerRequest {
  name: string | null
  cronExpression: string
  timeZone: string | null
}

export interface CreateJobRequest {
  type: string
  name: string
  group: string | null
  description: string | null
  data: Record<string, string>
  triggers: TriggerRequest[]
  retry: JobRetryPolicy
}

export interface UpdateJobRequest {
  description: string | null
  data: Record<string, string>
  triggers: TriggerRequest[]
  /** maxRetries 0 turns retries off, even when the job type has a policy */
  retry: JobRetryPolicy
}

/** Runs a job now; data replaces saved values for this run and its retries only */
export interface RunJobRequest {
  data: Record<string, string>
  /** The failed run this one retries, for the audit log */
  retryOf?: string
}

export type JobAction = 'trigger' | 'pause' | 'resume'

export type ExecutionStatus =
  | 'Running'
  | 'Succeeded'
  | 'Failed'
  | 'Vetoed'
  | 'Cancelled'
  /** Failed, and another attempt is scheduled */
  | 'Retried'
  /** Not a run: the trigger missed its fire time (scheduledFireTimeUtc); error says what Quartz did */
  | 'Misfired'

export interface ExecutionRecord {
  id: number
  schedulerName: string
  fireInstanceId: string
  instanceName: string
  jobName: string
  jobGroup: string
  jobType: string | null
  triggerName: string | null
  triggerGroup: string | null
  scheduledFireTimeUtc: string | null
  firedTimeUtc: string
  finishedTimeUtc: string | null
  durationMs: number | null
  status: ExecutionStatus
  error: string | null
  refireCount: number
  /** 1 for a first attempt, 2 for its first automatic retry, and so on */
  attempt: number
}

export interface HistoryPage {
  records: ExecutionRecord[]
  nextBefore: number | null
}

export interface HistorySummary {
  sinceUtc: string
  succeeded: number
  failed: number
  vetoed: number
  running: number
}

export interface HistoryFilter {
  status: ExecutionStatus | 'all'
  job: { group: string; name: string } | null
}
