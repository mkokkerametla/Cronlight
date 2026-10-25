import { Lock } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { jobStatus } from '@/lib/format'
import { cn } from '@/lib/utils'
import type { ExecutionStatus, Job, TriggerState } from '@/types'

const dot = 'size-1.5 rounded-full'

export function StatusBadge({ job }: { job: Job }) {
  switch (jobStatus(job)) {
    case 'Unloadable':
      return (
        <Badge variant="outline" className="gap-1 text-muted-foreground">
          <Lock className="size-3" /> Read-only
        </Badge>
      )
    case 'Paused':
      return (
        <Badge
          variant="outline"
          className="gap-1.5 border-amber-500/40 text-amber-700 dark:text-amber-400"
        >
          <span className={cn(dot, 'bg-amber-500')} /> Paused
        </Badge>
      )
    default:
      return (
        <Badge
          variant="outline"
          className="gap-1.5 border-emerald-500/40 text-emerald-700 dark:text-emerald-400"
        >
          <span className={cn(dot, 'bg-emerald-500')} /> Active
        </Badge>
      )
  }
}

const triggerTone: Partial<Record<TriggerState, string>> = {
  Normal: 'border-emerald-500/40 text-emerald-700 dark:text-emerald-400',
  Paused: 'border-amber-500/40 text-amber-700 dark:text-amber-400',
  Error: 'border-destructive/50 text-destructive',
  Blocked: 'border-sky-500/40 text-sky-700 dark:text-sky-400',
}

export function TriggerStateBadge({ state }: { state: TriggerState }) {
  return (
    <Badge variant="outline" className={triggerTone[state] ?? 'text-muted-foreground'}>
      {state}
    </Badge>
  )
}

const executionTone: Record<ExecutionStatus, string> = {
  Succeeded: 'border-emerald-500/40 text-emerald-700 dark:text-emerald-400',
  Failed: 'border-destructive/50 text-destructive',
  Running: 'border-sky-500/40 text-sky-700 dark:text-sky-400',
  Vetoed: 'text-muted-foreground',
  Cancelled: 'border-amber-500/40 text-amber-700 dark:text-amber-400',
  Retried: 'border-orange-500/40 text-orange-700 dark:text-orange-400',
}

export function ExecutionStatusBadge({ status }: { status: ExecutionStatus }) {
  return (
    <Badge variant="outline" className={cn('gap-1.5', executionTone[status])}>
      {status === 'Running' && <span className={cn(dot, 'animate-pulse bg-sky-500')} />}
      {status}
    </Badge>
  )
}
