import type { ExecutionStatus } from '@/types'

/**
 * The fill of a run's bar, by its outcome, alike in the job list's run strip and a job's duration chart. Outcomes mean
 * good or bad, so they wear status colours, a shade darker on white so a bar keeps 3:1 against it.
 */
export const runFill: Partial<Record<ExecutionStatus, string>> = {
  Succeeded: 'bg-emerald-600 dark:bg-emerald-500',
  Failed: 'bg-destructive',
  Retried: 'bg-orange-600 dark:bg-orange-500',
  Cancelled: 'bg-amber-600 dark:bg-amber-500',
  Running: 'bg-sky-600 dark:bg-sky-500',
}

/** The outline of a run in which nothing ran: drawn hollow */
export const runOutline: Partial<Record<ExecutionStatus, string>> = {
  Vetoed: 'border-muted-foreground',
  Misfired: 'border-violet-600 dark:border-violet-400',
}
