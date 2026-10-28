import { Ban, CircleCheck, CircleSlash, CircleX, Clock, type LucideIcon } from 'lucide-react'
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip'
import { formatDateTime, formatDuration } from '@/lib/format'
import { cn } from '@/lib/utils'
import type { ExecutionStatus, RecentRun } from '@/types'

/** How many of a job's last runs the job list shows, as the server sends them */
export const RECENT_RUNS = 5

/** Shape and colour both, so each outcome reads without colour too. Running is a pulsing dot. */
const outcome: Record<Exclude<ExecutionStatus, 'Running'>, { icon: LucideIcon; tone: string }> = {
  Succeeded: { icon: CircleCheck, tone: 'text-emerald-600 dark:text-emerald-400' },
  Failed: { icon: CircleX, tone: 'text-destructive' },
  // A pending retry: the run's last attempt so far failed
  Retried: { icon: CircleX, tone: 'text-orange-600 dark:text-orange-400' },
  Cancelled: { icon: CircleSlash, tone: 'text-amber-600 dark:text-amber-400' },
  Vetoed: { icon: Ban, tone: 'text-muted-foreground' },
  Misfired: { icon: Clock, tone: 'text-violet-600 dark:text-violet-400' },
}

const label: Record<ExecutionStatus, string> = {
  Running: 'Running',
  Succeeded: 'Succeeded',
  Failed: 'Failed',
  Retried: 'Failed (retry waiting)',
  Cancelled: 'Cancelled',
  Vetoed: 'Blocked',
  Misfired: 'Missed',
}

function describe(run: RecentRun) {
  const parts = [label[run.status], formatDateTime(run.firedTimeUtc)]
  if (run.durationMs !== null) parts.push(formatDuration(run.durationMs))
  if (run.attempt > 1) parts.push(`attempt ${run.attempt}`)
  return parts.join(' · ')
}

/** A job's last runs as icons, oldest on the left and newest on the right, with empty slots when it has fewer */
export function RecentRuns({ runs }: { runs: RecentRun[] }) {
  const shown = runs.slice(0, RECENT_RUNS).reverse()
  const empty = RECENT_RUNS - shown.length
  return (
    <div
      className="flex items-center gap-0.5"
      role="list"
      aria-label={
        shown.length === 0
          ? 'No runs yet'
          : `${shown.length === 1 ? 'Last run' : `Last ${shown.length} runs, oldest first`}: ${shown.map(r => label[r.status].toLowerCase()).join(', ')}`
      }
    >
      {Array.from({ length: empty }, (_, i) => (
        <span
          key={`empty-${i}`}
          aria-hidden
          className="m-0.5 size-3 rounded-full border border-dashed border-muted-foreground/40"
        />
      ))}
      {shown.map(run => (
        <Tooltip key={run.id}>
          <TooltipTrigger asChild>
            <span role="listitem" className="relative flex size-4 items-center justify-center">
              <RunIcon status={run.status} />
              {/* Earlier attempts failed and were retried */}
              {run.attempt > 1 && (
                <span className="absolute -top-0.5 -right-0.5 size-1.5 rounded-full bg-orange-500 ring-1 ring-background" />
              )}
            </span>
          </TooltipTrigger>
          <TooltipContent>{describe(run)}</TooltipContent>
        </Tooltip>
      ))}
    </div>
  )
}

function RunIcon({ status }: { status: ExecutionStatus }) {
  if (status === 'Running')
    return <span className="size-2.5 animate-pulse rounded-full bg-sky-500" aria-label="Running" />
  const { icon: Icon, tone } = outcome[status]
  return <Icon className={cn('size-4', tone)} aria-label={label[status]} />
}
