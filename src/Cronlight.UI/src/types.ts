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
}

export interface UpdateJobRequest {
  description: string | null
  data: Record<string, string>
  triggers: TriggerRequest[]
}

export type JobAction = 'trigger' | 'pause' | 'resume'

export type ExecutionStatus = 'Running' | 'Succeeded' | 'Failed' | 'Vetoed'

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
