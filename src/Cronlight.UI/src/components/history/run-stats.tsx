import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { Skeleton } from '@/components/ui/skeleton'
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip'
import { useRunStats } from '@/hooks/use-run-stats'
import { formatDateTime, formatDuration, formatRate, statsPeriods } from '@/lib/format'
import { runFill } from '@/lib/run-tones'
import { cn } from '@/lib/utils'
import type { ExecutionRecord, ExecutionStatus, RunStatistics } from '@/types'

const rateTone = (rate: number | null) =>
  rate === null || rate >= 0.99
    ? undefined
    : rate >= 0.9
      ? 'text-amber-700 dark:text-amber-400'
      : 'text-destructive'

/** What the figures leave to a second line: retries, misfires and late starts, when there are any */
function extras(s: RunStatistics) {
  const plural = (n: number, one: string, many: string) => `${n} ${n === 1 ? one : many}`
  return [
    s.retries > 0 && plural(s.retries, 'retry', 'retries'),
    s.vetoed > 0 && `${s.vetoed} blocked`,
    s.cancelled > 0 && `${s.cancelled} cancelled`,
    s.misfires > 0 && plural(s.misfires, 'misfire', 'misfires'),
    s.lateStarts > 0 && plural(s.lateStarts, 'late start', 'late starts'),
    s.running > 0 && `${s.running} running`,
  ].filter(Boolean)
}

/**
 * Runs, success rate, failures and average and P95 duration over the period, for the job History is filtered to or for
 * every job. The outcome filter applies to the list only: a success rate over failures alone says nothing.
 */
export function RunStatsRow({
  job,
  hours,
  onHoursChange,
  filtered = false,
}: {
  job: { group: string; name: string } | null
  hours: number
  /** Changes the period, from a select beside the figures: it changes them alone, not the list of runs */
  onHoursChange?: (hours: number) => void
  /** The list is filtered to one outcome, which the figures are not */
  filtered?: boolean
}) {
  const { stats, error } = useRunStats(job, hours)
  if (error) return <p className="text-sm text-destructive">{error}</p>
  const s = stats?.stats
  const figures = [
    { label: 'Runs', value: s ? s.runs.toLocaleString() : null },
    {
      label: 'Success rate',
      value: s ? formatRate(s.successRate) : null,
      tone: s && rateTone(s.successRate),
    },
    {
      label: 'Failures',
      value: s ? (s.failed + s.vetoed).toLocaleString() : null,
      tone: s && s.failed + s.vetoed > 0 ? 'text-destructive' : undefined,
    },
    { label: 'Average duration', value: s ? formatDuration(s.averageDurationMs) : null },
    { label: 'P95 duration', value: s ? formatDuration(s.p95DurationMs) : null },
  ]

  return (
    <section aria-label="Run statistics" className="space-y-1.5">
      {onHoursChange && (
        <Select value={String(hours)} onValueChange={v => onHoursChange(Number(v))}>
          <SelectTrigger size="sm" className="w-40" aria-label="Period of the figures">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            {statsPeriods.map(p => (
              <SelectItem key={p.hours} value={String(p.hours)}>
                {p.label}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
      )}
      <dl className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-5">
        {figures.map(f => (
          <div key={f.label} className="rounded-lg border px-3 py-2">
            <dt className="text-xs text-muted-foreground">{f.label}</dt>
            <dd className={cn('text-lg font-semibold tabular-nums', f.tone)}>
              {f.value ?? <Skeleton className="my-1 h-5 w-14" />}
            </dd>
          </div>
        ))}
      </dl>
      {s && (filtered || extras(s).length > 0 || s.truncated) && (
        <p className="text-xs text-muted-foreground">
          {[
            filtered && 'Over every outcome',
            ...extras(s),
            s.truncated && 'counted over the newest 20,000 runs only',
          ]
            .filter(Boolean)
            .join(' · ')}
        </p>
      )}
    </section>
  )
}

/** A job's success rate and durations over the last day and the last week, and its recent runs' durations */
export function JobRunStats({
  group,
  name,
  records,
}: {
  group: string
  name: string
  /** Its recent runs, newest first */
  records: ExecutionRecord[]
}) {
  const dayStats = useRunStats({ group, name }, 24)
  const weekStats = useRunStats({ group, name }, 24 * 7)
  const day = dayStats.stats?.stats
  const week = weekStats.stats?.stats
  const error = dayStats.error ?? weekStats.error
  const rows: { label: string; value: (s: RunStatistics) => string; tone?: typeof rateTone }[] = [
    { label: 'Runs', value: s => String(s.runs) },
    { label: 'Success rate', value: s => formatRate(s.successRate), tone: rateTone },
    { label: 'Average duration', value: s => formatDuration(s.averageDurationMs) },
    { label: 'P95 duration', value: s => formatDuration(s.p95DurationMs) },
  ]

  return (
    <div className="space-y-3">
      {error ? (
        <p className="text-sm text-destructive">{error}</p>
      ) : (
        <table className="w-full text-sm">
          <thead>
            <tr className="text-xs text-muted-foreground">
              <th className="text-left font-normal">
                <span className="sr-only">Figure</span>
              </th>
              <th className="text-right font-normal">24 hours</th>
              <th className="text-right font-normal">7 days</th>
            </tr>
          </thead>
          <tbody>
            {rows.map(row => (
              <tr key={row.label}>
                <th scope="row" className="py-0.5 text-left font-normal text-muted-foreground">
                  {row.label}
                </th>
                {[day, week].map((s, i) => (
                  <td
                    key={i}
                    className={cn('text-right tabular-nums', s && row.tone?.(s.successRate))}
                  >
                    {s ? row.value(s) : <Skeleton className="ml-auto h-4 w-10" />}
                  </td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
      )}
      <DurationChart records={records} />
    </div>
  )
}

/**
 * The recent runs' durations, oldest on the left, each bar coloured by its outcome as in the job list's run strip and
 * named on hover. Only runs with a duration: one going on has none yet, and a missed or blocked run never ran.
 */
export function DurationChart({ records }: { records: ExecutionRecord[] }) {
  const runs = records.filter(r => r.durationMs !== null && runFill[r.status]).reverse()
  if (runs.length < 2) return null
  const longest = Math.max(...runs.map(r => r.durationMs!), 1)
  const outcomes = Object.keys(runFill).filter(o => runs.some(r => r.status === o))

  return (
    <figure className="space-y-1.5">
      <figcaption className="flex items-baseline justify-between text-xs text-muted-foreground">
        <span>Duration of the last {runs.length} runs</span>
        <span className="tabular-nums">longest {formatDuration(longest)}</span>
      </figcaption>
      <div
        className="flex h-16 items-end gap-0.5 border-b border-border"
        role="img"
        aria-label={`Durations of the last ${runs.length} runs, oldest first: ${runs
          .map(r => `${formatDuration(r.durationMs)} ${r.status.toLowerCase()}`)
          .join(', ')}`}
      >
        {runs.map(r => (
          <Tooltip key={r.id}>
            <TooltipTrigger asChild>
              {/* The whole column is the hover target, so a short bar is as easy to point at as a tall one */}
              <div className="flex h-full max-w-3 min-w-0 flex-1 items-end">
                <div
                  className={cn('w-full rounded-t-[4px]', runFill[r.status])}
                  style={{ height: `${Math.max(3, (r.durationMs! / longest) * 100)}%` }}
                />
              </div>
            </TooltipTrigger>
            <TooltipContent>
              {r.status} in {formatDuration(r.durationMs)} · {formatDateTime(r.firedTimeUtc)}
            </TooltipContent>
          </Tooltip>
        ))}
      </div>
      {outcomes.length > 0 && (
        <ul className="flex flex-wrap gap-x-3 gap-y-1 text-xs text-muted-foreground">
          {outcomes.map(o => (
            <li key={o} className="inline-flex items-center gap-1">
              <span className={cn('size-2 rounded-[2px]', runFill[o as ExecutionStatus])} />
              {o}
            </li>
          ))}
        </ul>
      )}
    </figure>
  )
}
