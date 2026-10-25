import type { JobParameter, JobRetryPolicy, JobType, Trigger, TriggerRequest } from '@/types'

// Form state <-> API request conversion shared by the create and edit forms

export const MASK = '********'
/** Values starting with this name a secret in the server's configuration (see SecretsSection) */
export const SECRET_PREFIX = 'secret:'

/** A JobDataMap entry. Rows with a param come from [JobParameter] metadata and have a fixed key. */
export interface DataRow {
  key: string
  value: string
  param?: JobParameter
}

/** Known parameters first (in declaration order), then any other keys already in the JobDataMap. */
export function dataRows(
  jobType: JobType | undefined,
  data: Record<string, string | null>
): DataRow[] {
  const params = jobType?.parameters ?? []
  const rows: DataRow[] = params.map(p => ({
    key: p.key,
    value: data[p.key] ?? p.defaultValue ?? '',
    param: p,
  }))
  for (const [key, value] of Object.entries(data)) {
    if (!params.some(p => p.key === key)) rows.push({ key, value: value ?? '' })
  }
  return rows
}

export const rowsToData = (rows: DataRow[]): Record<string, string> =>
  Object.fromEntries(
    rows.filter(r => r.key.trim() && (r.value !== '' || r.param)).map(r => [r.key.trim(), r.value])
  )

export interface TriggerDraft {
  /** Client-only row key, so per-row state (cron preview, schedule assistant) stays with its trigger */
  id: number
  name: string
  cronExpression: string
  timeZone: string
  /** Skip a fire time the scheduler missed, instead of running once as soon as it can */
  skipMissed: boolean
}

let lastDraftId = 0
const nextDraftId = () => ++lastDraftId

export const emptyTrigger = (): TriggerDraft => ({
  id: nextDraftId(),
  name: '',
  cronExpression: '',
  timeZone: '',
  skipMissed: false,
})

export const draftsFromTriggers = (triggers: Trigger[]): TriggerDraft[] => {
  const cron = triggers
    .filter(t => t.kind === 'Cron')
    .map(t => ({
      id: nextDraftId(),
      name: t.name,
      cronExpression: t.cronExpression ?? '',
      timeZone: t.timeZone === 'UTC' ? '' : (t.timeZone ?? ''),
      skipMissed: t.misfireInstruction === 'DoNothing',
    }))
  return cron.length ? cron : [emptyTrigger()]
}

export const draftsToRequest = (drafts: TriggerDraft[]): TriggerRequest[] =>
  drafts
    .filter(t => t.cronExpression.trim())
    .map(t => ({
      name: t.name.trim() || null,
      cronExpression: t.cronExpression.trim(),
      timeZone: t.timeZone.trim() || null,
      // Left out when off: Quartz's default, running once as soon as it can
      ...(t.skipMissed && { skipMissed: true }),
    }))

/** Retry fields as typed, so a field can be empty while it is edited */
export interface RetryDraft {
  maxRetries: string
  delaySeconds: string
  exponential: boolean
  /** Exception type names, comma separated; empty retries every failure */
  retryOn: string
}

export const retryDraft = (policy: JobRetryPolicy | null | undefined): RetryDraft => ({
  maxRetries: String(policy?.maxRetries ?? 0),
  delaySeconds: String(policy?.delaySeconds ?? 60),
  exponential: policy?.exponential ?? false,
  retryOn: (policy?.retryOn ?? []).join(', '),
})

export const draftToRetry = (draft: RetryDraft): JobRetryPolicy => {
  const retryOn = draft.retryOn
    .split(',')
    .map(t => t.trim())
    .filter(Boolean)
  return {
    maxRetries: Number(draft.maxRetries) || 0,
    delaySeconds: Number(draft.delaySeconds) || 0,
    exponential: draft.exponential,
    ...(retryOn.length > 0 && { retryOn }),
  }
}

/** Values that differ from the saved job data, for a run with different data. Masked secrets are unchanged. */
export const changedData = (
  rows: DataRow[],
  saved: Record<string, string | null>
): Record<string, string> =>
  Object.fromEntries(
    Object.entries(rowsToData(rows)).filter(
      ([key, value]) => value !== MASK && value !== saved[key]
    )
  )
