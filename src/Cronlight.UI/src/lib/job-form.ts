import type {
  EditableTriggerKind,
  JobParameter,
  JobRetryPolicy,
  JobType,
  Trigger,
  TriggerRequest,
} from '@/types'

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

export type IntervalUnit = 'seconds' | 'minutes' | 'hours' | 'days'

export const unitSeconds: Record<IntervalUnit, number> = {
  seconds: 1,
  minutes: 60,
  hours: 3600,
  days: 86_400,
}

export const editableKinds: EditableTriggerKind[] = ['Cron', 'Once', 'Interval']

export const isEditable = (trigger: Trigger) => (editableKinds as string[]).includes(trigger.kind)

export interface TriggerDraft {
  /** Client-only row key, so per-row state (cron preview) stays with its trigger */
  id: number
  kind: EditableTriggerKind
  name: string
  /** Cron */
  cronExpression: string
  /** Cron */
  timeZone: string
  /** Once and Interval: a datetime-local value, in the viewer's time zone */
  startAt: string
  /**
   * The stored start time and how startAt first showed it. While startAt still shows it, the exact stored time is sent
   * back, so a trigger left as it was is not rescheduled.
   */
  storedStart: { local: string; iso: string } | null
  /** Interval: how many units between runs */
  every: string
  unit: IntervalUnit
  /** Interval: how many runs in all; empty repeats forever */
  runs: string
  /** Skip a fire time the scheduler missed, instead of running once as soon as it can */
  skipMissed: boolean
}

let lastDraftId = 0
const nextDraftId = () => ++lastDraftId

export const emptyTrigger = (kind: EditableTriggerKind = 'Cron'): TriggerDraft => ({
  id: nextDraftId(),
  kind,
  name: '',
  cronExpression: '',
  timeZone: '',
  startAt: '',
  storedStart: null,
  every: '',
  unit: 'minutes',
  runs: '',
  skipMissed: false,
})

const pad = (n: number) => String(n).padStart(2, '0')

/** A time as a datetime-local input shows it, in the viewer's time zone, to the minute */
export function toLocalInput(iso: string): string {
  const d = new Date(iso)
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`
}

/** A datetime-local value (read in the viewer's time zone) as UTC, or null when it is not a time */
export function fromLocalInput(value: string): string | null {
  const time = new Date(value)
  return value && !Number.isNaN(time.getTime()) ? time.toISOString() : null
}

/** The largest unit the interval is a whole number of */
const intervalUnit = (seconds: number): IntervalUnit =>
  (['days', 'hours', 'minutes'] as const).find(u => seconds % unitSeconds[u] === 0) ?? 'seconds'

export const draftsFromTriggers = (triggers: Trigger[]): TriggerDraft[] => {
  const drafts = triggers.filter(isEditable).map((t): TriggerDraft => {
    const local = t.startTimeUtc ? toLocalInput(t.startTimeUtc) : ''
    const unit = intervalUnit(t.intervalSeconds ?? 60)
    return {
      ...emptyTrigger(t.kind as EditableTriggerKind),
      name: t.name,
      cronExpression: t.cronExpression ?? '',
      timeZone: t.timeZone === 'UTC' ? '' : (t.timeZone ?? ''),
      startAt: local,
      storedStart: t.startTimeUtc ? { local, iso: t.startTimeUtc } : null,
      every: t.intervalSeconds ? String(t.intervalSeconds / unitSeconds[unit]) : '',
      unit,
      runs: t.repeatCount != null ? String(t.repeatCount + 1) : '',
      skipMissed: t.skipsMissed ?? t.misfireInstruction === 'DoNothing',
    }
  })
  return drafts.length ? drafts : [emptyTrigger()]
}

const startAtUtc = (t: TriggerDraft) =>
  t.storedStart && t.startAt === t.storedStart.local ? t.storedStart.iso : fromLocalInput(t.startAt)

const positive = (text: string) => {
  const n = Number(text.trim())
  return Number.isInteger(n) && n > 0 ? n : null
}

/** Rows not filled in enough to say when they run are left out, as an empty cron row always was */
const complete = (t: TriggerDraft) =>
  t.kind === 'Once'
    ? startAtUtc(t) !== null
    : t.kind === 'Interval'
      ? positive(t.every) !== null
      : t.cronExpression.trim() !== ''

export const draftsToRequest = (drafts: TriggerDraft[]): TriggerRequest[] =>
  drafts.filter(complete).map(t => {
    const common = {
      name: t.name.trim() || null,
      // Left out when off: Quartz's default, running once as soon as it can
      ...(t.skipMissed && { skipMissed: true }),
    }
    if (t.kind === 'Once') return { ...common, kind: 'Once', startAtUtc: startAtUtc(t)! }
    if (t.kind === 'Interval') {
      const start = startAtUtc(t)
      const runs = positive(t.runs)
      return {
        ...common,
        kind: 'Interval',
        intervalSeconds: positive(t.every)! * unitSeconds[t.unit],
        ...(start && { startAtUtc: start }),
        ...(runs !== null && { repeatCount: runs - 1 }),
      }
    }
    return {
      ...common,
      cronExpression: t.cronExpression.trim(),
      timeZone: t.timeZone.trim() || null,
    }
  })

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

/**
 * What an edit changes in the job data, compared with the form as it opened (and the data it was opened from): entries
 * added or changed, and entries removed, as null. Entries left as they were are not sent, so the job keeps what it has
 * now, even if it changed meanwhile (a job that saves its own data as it runs, say). The one exception is a required
 * parameter the job does not hold yet: its default is sent, so the server does not find it missing.
 */
export function dataChanges(
  rows: DataRow[],
  opened: DataRow[],
  saved: Record<string, string | null>
): Record<string, string | null> {
  const before = rowsToData(opened)
  const after = rowsToData(rows)
  const required = new Set(rows.filter(r => r.param?.required).map(r => r.key.trim()))
  // Own keys only, and no prototype: job data can hold a key such as "constructor" or "__proto__"
  const changes: Record<string, string | null> = Object.create(null)
  for (const [key, value] of Object.entries(after)) {
    const changed = !Object.hasOwn(before, key) || before[key] !== value
    const requiredDefault = required.has(key) && !Object.hasOwn(saved, key) && value !== ''
    if (changed || requiredDefault) changes[key] = value
  }
  for (const key of Object.keys(before)) if (!Object.hasOwn(after, key)) changes[key] = null
  return changes
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
