import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip'
import { formatDateTime, formatDuration } from '@/lib/format'
import { runFill, runOutline } from '@/lib/run-tones'
import { cn } from '@/lib/utils'
import type { ExecutionStatus, RecentRun } from '@/types'

/** How many of a job's last runs the job list shows, as the server sends them */
export const RECENT_RUNS = 10

// A run that ran is drawn as tall as it took, against the longest of those shown, between these shares of the strip.
// Failures, waiting retries and runs that did not run reach the top whatever they took, so the top marks a problem by
// shape as well as colour.
const SHORTEST = 20
const TALLEST = 75

const label: Record<ExecutionStatus, string> = {
  Running: 'Running',
  Succeeded: 'Succeeded',
  Failed: 'Failed',
  Retried: 'Failed (retry waiting)',
  Cancelled: 'Cancelled',
  Vetoed: 'Blocked',
  Misfired: 'Missed',
}

const problem: ExecutionStatus[] = ['Failed', 'Retried', 'Vetoed', 'Misfired']

function describe(run: RecentRun) {
  const parts = [label[run.status], formatDateTime(run.firedTimeUtc)]
  if (run.durationMs !== null) parts.push(formatDuration(run.durationMs))
  if (run.attempt > 1) parts.push(`attempt ${run.attempt}`)
  return parts.join(' · ')
}

/**
 * A job's last runs as a strip of bars, oldest on the left: each as tall as it took and coloured by its outcome, as in
 * the job's duration chart, with problems reaching the top. Short ticks stand for runs a job has not had yet.
 */
export function RecentRuns({ runs }: { runs: RecentRun[] }) {
  const shown = runs.slice(0, RECENT_RUNS).reverse()
  const empty = RECENT_RUNS - shown.length
  const longest = Math.max(
    1,
    ...shown.filter(r => !problem.includes(r.status)).map(r => r.durationMs ?? 0)
  )
  const height = (run: RecentRun) =>
    problem.includes(run.status)
      ? 100
      : run.durationMs === null
        ? TALLEST
        : Math.max(SHORTEST, (run.durationMs / longest) * TALLEST)

  return (
    <div
      className="flex h-5 w-fit items-end gap-0.5 border-b border-border"
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
          data-shape="empty"
          className="mb-px h-0.5 w-1.5 rounded-full bg-muted-foreground/30"
        />
      ))}
      {shown.map(run => {
        const hollow = runOutline[run.status]
        return (
          <Tooltip key={run.id}>
            <TooltipTrigger asChild>
              {/* The whole column is the hover target, so a short bar is as easy to point at as a tall one */}
              <span
                role="listitem"
                aria-label={
                  run.attempt > 1
                    ? `${label[run.status]}, attempt ${run.attempt}`
                    : label[run.status]
                }
                className="flex h-full w-1.5 items-end"
              >
                <span
                  data-shape={hollow ? 'hollow' : 'bar'}
                  className={cn(
                    'relative w-full rounded-t-[2px]',
                    hollow ? cn('border border-b-0', hollow) : runFill[run.status]
                  )}
                  style={{ height: `${height(run)}%` }}
                >
                  {/* Earlier attempts failed and were retried */}
                  {run.attempt > 1 && (
                    <span
                      data-retried
                      className="absolute inset-x-0 -top-1 h-0.5 rounded-full bg-orange-500"
                    />
                  )}
                </span>
              </span>
            </TooltipTrigger>
            <TooltipContent>{describe(run)}</TooltipContent>
          </Tooltip>
        )
      })}
    </div>
  )
}
