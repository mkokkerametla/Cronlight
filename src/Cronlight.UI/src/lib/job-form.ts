import type { JobParameter, JobType, Trigger, TriggerRequest } from '@/types'

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
}

let lastDraftId = 0
const nextDraftId = () => ++lastDraftId

export const emptyTrigger = (): TriggerDraft => ({
  id: nextDraftId(),
  name: '',
  cronExpression: '',
  timeZone: '',
})

export const draftsFromTriggers = (triggers: Trigger[]): TriggerDraft[] => {
  const cron = triggers
    .filter(t => t.kind === 'Cron')
    .map(t => ({
      id: nextDraftId(),
      name: t.name,
      cronExpression: t.cronExpression ?? '',
      timeZone: t.timeZone === 'UTC' ? '' : (t.timeZone ?? ''),
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
    }))
