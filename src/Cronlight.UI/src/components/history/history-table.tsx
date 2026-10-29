import { Fragment, useState } from 'react'
import { ArrowUp, History, RotateCcw, ScrollText, X } from 'lucide-react'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { Skeleton } from '@/components/ui/skeleton'
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table'
import { RunLog } from '@/components/history/run-log'
import { RunStatsRow } from '@/components/history/run-stats'
import { ExecutionStatusBadge } from '@/components/jobs/status-badge'
import { useHistory } from '@/hooks/use-history'
import { useRefreshInterval } from '@/hooks/use-polling'
import {
  everySeconds,
  formatDateTime,
  formatDuration,
  formatRelative,
  recordTime,
  shortType,
  startedLate,
} from '@/lib/format'
import { cn } from '@/lib/utils'
import type { ExecutionRecord, ExecutionStatus, HistoryFilter, JobType } from '@/types'

interface Props {
  filter: HistoryFilter
  onFilterChange: (filter: HistoryFilter) => void
  onOpenJob: (group: string, name: string) => void
  canManage?: boolean
  /** Runs the job again; resolves to true when it was started */
  onRetry?: (record: ExecutionRecord) => Promise<boolean>
  /** The job types the dashboard describes, so a run shows its type's name as the job list does */
  jobTypes?: JobType[]
  /** This node's scheduler instance id: only the node running a job has its log so far */
  localInstanceId?: string
  /**
   * The scheduler runs as a cluster. Otherwise every run is on this node, so the Node column is shown only when the
   * runs listed came from more than one (a cluster earlier, say).
   */
  clustered?: boolean
}

// Runs that ended without succeeding and will not be retried automatically
const retryable: ExecutionStatus[] = ['Failed', 'Vetoed', 'Cancelled']

export function HistoryTable({
  filter,
  onFilterChange,
  onOpenJob,
  canManage = false,
  onRetry,
  jobTypes = [],
  localInstanceId,
  clustered = true,
}: Props) {
  // A run going on here shows its log so far; one on another node only once it has finished
  const liveLog = (r: ExecutionRecord) =>
    r.status === 'Running' && localInstanceId !== undefined && r.instanceName === localInstanceId
  const logged = (r: ExecutionRecord) => r.hasLog || liveLog(r)
  const typeName = (type: string | null) =>
    jobTypes.find(t => t.typeName === type)?.displayName ?? shortType(type)
  const {
    records,
    hasMore,
    loadMore,
    error,
    loaded,
    showRunSoon,
    newRuns,
    gone,
    showNewRuns,
    holdNewRuns,
  } = useHistory(filter)
  const refreshMs = useRefreshInterval()
  const [openLog, setOpenLog] = useState<number | null>(null)
  const [hours, setHours] = useState(24)
  const showNode = clustered || new Set(records.map(r => r.instanceName)).size > 1
  const columns = 4 + (showNode ? 1 : 0) + (canManage && onRetry ? 1 : 0)

  return (
    <div className="space-y-3">
      <div className="flex flex-wrap items-center gap-2">
        <Select
          value={filter.status}
          onValueChange={v => onFilterChange({ ...filter, status: v as ExecutionStatus | 'all' })}
        >
          <SelectTrigger className="w-40" aria-label="Filter by outcome">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="all">All outcomes</SelectItem>
            <SelectItem value="Failed">Failed</SelectItem>
            <SelectItem value="Retried">Retried</SelectItem>
            <SelectItem value="Succeeded">Succeeded</SelectItem>
            <SelectItem value="Running">Running</SelectItem>
            <SelectItem value="Cancelled">Cancelled</SelectItem>
            <SelectItem value="Vetoed">Vetoed</SelectItem>
            <SelectItem value="Misfired">Misfired</SelectItem>
          </SelectContent>
        </Select>
        {filter.job && (
          <Badge variant="secondary" className="gap-1 py-1">
            {filter.job.group}.{filter.job.name}
            <button
              type="button"
              aria-label="Show all jobs"
              className="rounded-sm hover:text-foreground"
              onClick={() => onFilterChange({ ...filter, job: null })}
            >
              <X className="size-3" />
            </button>
          </Badge>
        )}
        <span className="ml-auto text-sm text-muted-foreground">
          Newest first · refreshes {everySeconds(refreshMs / 1000)}
        </span>
      </div>

      <RunStatsRow
        job={filter.job}
        hours={hours}
        onHoursChange={setHours}
        filtered={filter.status !== 'all'}
      />

      {error && (
        <Alert variant="destructive">
          <AlertDescription>{error}</AlertDescription>
        </Alert>
      )}

      {/* New runs wait while the pointer is over the list, so a click lands on the run aimed at. Only the list: the
          filters above it never move, and a filter just chosen leaves the pointer over the list without entering it */}
      <div
        className="overflow-hidden rounded-lg border"
        onPointerEnter={() => holdNewRuns(true)}
        onPointerLeave={() => holdNewRuns(false)}
      >
        {newRuns > 0 && (
          <button
            type="button"
            onClick={showNewRuns}
            className="flex w-full items-center justify-center gap-1.5 border-b bg-muted/50 py-1.5 text-sm font-medium text-link hover:bg-muted focus-visible:bg-muted focus-visible:outline-none"
          >
            <ArrowUp className="size-4" /> Show {newRuns} new {newRuns === 1 ? 'run' : 'runs'}
          </button>
        )}
        {!loaded ? (
          <div className="space-y-2 p-3">
            {Array.from({ length: 5 }, (_, i) => (
              <Skeleton key={i} className="h-9" />
            ))}
          </div>
        ) : records.length === 0 ? (
          <div className="flex flex-col items-center gap-2 py-14 text-center">
            <History className="size-8 text-muted-foreground" />
            <p className="font-medium">No executions recorded</p>
            <p className="text-sm text-muted-foreground">
              {filter.status !== 'all' || filter.job
                ? 'Nothing matches these filters.'
                : 'Runs appear here as soon as jobs execute.'}
            </p>
          </div>
        ) : (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Job</TableHead>
                <TableHead>Outcome</TableHead>
                <TableHead>Fired</TableHead>
                <TableHead className="text-right">Duration</TableHead>
                {showNode && <TableHead>Node</TableHead>}
                {canManage && onRetry && (
                  <TableHead>
                    <span className="sr-only">Actions</span>
                  </TableHead>
                )}
              </TableRow>
            </TableHeader>
            <TableBody>
              {records.map(r => (
                <Fragment key={r.id}>
                  <TableRow
                    className={cn('group align-top', gone.has(r.id) && 'opacity-50')}
                    title={
                      gone.has(r.id)
                        ? 'No longer listed: it goes when the pointer leaves the list'
                        : undefined
                    }
                  >
                    <TableCell className="max-w-md whitespace-normal">
                      <button
                        type="button"
                        className="text-left font-medium hover:underline"
                        onClick={() => onOpenJob(r.jobGroup, r.jobName)}
                      >
                        {r.jobName}
                      </button>
                      <div className="text-xs text-muted-foreground">
                        {r.jobGroup} · {typeName(r.jobType)}
                        {r.refireCount > 0 && ` · refire ${r.refireCount}`}
                        {r.attempt > 1 && ` · attempt ${r.attempt}`}
                        {startedLate(r) && (
                          <span className="text-amber-700 dark:text-amber-400">
                            {` · started ${startedLate(r)} late`}
                          </span>
                        )}
                        {r.data && (
                          <>
                            {' · '}
                            <span
                              className="underline decoration-dotted"
                              title={Object.entries(r.data)
                                .map(([k, v]) => `${k} = ${v}`)
                                .join('\n')}
                            >
                              own data
                            </span>
                          </>
                        )}
                        {logged(r) && (
                          <>
                            {' · '}
                            <button
                              type="button"
                              className="inline-flex items-center gap-0.5 hover:text-foreground hover:underline"
                              aria-expanded={openLog === r.id}
                              onClick={() => setOpenLog(openLog === r.id ? null : r.id)}
                            >
                              <ScrollText className="size-3" />
                              {openLog === r.id ? 'hide log' : liveLog(r) ? 'log so far' : 'log'}
                            </button>
                          </>
                        )}
                      </div>
                      {r.error && (
                        <p
                          // The outcome badge is red already: in a list of hundreds of failures, red errors on every row
                          // leave nothing standing out
                          className={cn(
                            'mt-1 line-clamp-2 text-xs break-words text-pretty',
                            r.status === 'Misfired' ? 'text-muted-foreground' : 'text-foreground/80'
                          )}
                          title={r.error}
                        >
                          {r.error}
                        </p>
                      )}
                    </TableCell>
                    <TableCell>
                      <ExecutionStatusBadge status={r.status} />
                    </TableCell>
                    <TableCell title={formatDateTime(recordTime(r))}>
                      {r.status === 'Misfired' && 'missed '}
                      {formatRelative(recordTime(r))}
                    </TableCell>
                    <TableCell className="text-right tabular-nums">
                      {formatDuration(r.durationMs)}
                    </TableCell>
                    {showNode && (
                      <TableCell className="font-mono text-xs text-muted-foreground">
                        {r.instanceName}
                      </TableCell>
                    )}
                    {canManage && onRetry && (
                      <TableCell className="text-right">
                        {retryable.includes(r.status) && (
                          // Shown on the row the pointer is over, or when focused; always where nothing hovers (touch)
                          <Button
                            size="sm"
                            variant="outline"
                            className="transition-opacity focus-visible:opacity-100 [@media(hover:hover)]:opacity-0 [@media(hover:hover)]:group-hover:opacity-100"
                            aria-label={`Run ${r.jobName} again`}
                            onClick={async () => {
                              if (await onRetry(r)) showRunSoon()
                            }}
                          >
                            <RotateCcw /> Run again
                          </Button>
                        )}
                      </TableCell>
                    )}
                  </TableRow>
                  {openLog === r.id && logged(r) && (
                    <TableRow className="hover:bg-transparent">
                      <TableCell colSpan={columns} className="whitespace-normal">
                        {/* Keyed apart, so a run that finishes loads its saved log afresh */}
                        {liveLog(r) ? (
                          <RunLog
                            key="live"
                            path={`running/${encodeURIComponent(r.fireInstanceId)}/log`}
                            live
                          />
                        ) : (
                          <RunLog key="saved" path={`history/${r.id}/log`} />
                        )}
                      </TableCell>
                    </TableRow>
                  )}
                </Fragment>
              ))}
            </TableBody>
          </Table>
        )}
      </div>

      {hasMore && (
        <div className="flex justify-center">
          <Button variant="outline" onClick={loadMore}>
            Load older runs
          </Button>
        </div>
      )}
    </div>
  )
}
