import { History, RotateCcw, X } from 'lucide-react'
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
import { ExecutionStatusBadge } from '@/components/jobs/status-badge'
import { useHistory } from '@/hooks/use-history'
import { formatDateTime, formatDuration, formatRelative, shortType } from '@/lib/format'
import type { ExecutionRecord, ExecutionStatus, HistoryFilter } from '@/types'

interface Props {
  filter: HistoryFilter
  onFilterChange: (filter: HistoryFilter) => void
  onOpenJob: (group: string, name: string) => void
  canManage?: boolean
  /** Runs the job again; resolves to true when it was started */
  onRetry?: (record: ExecutionRecord) => Promise<boolean>
}

// Runs that ended without succeeding and will not be retried automatically
const retryable: ExecutionStatus[] = ['Failed', 'Vetoed', 'Cancelled']

export function HistoryTable({
  filter,
  onFilterChange,
  onOpenJob,
  canManage = false,
  onRetry,
}: Props) {
  const { records, hasMore, loadMore, error, loaded, refresh } = useHistory(filter)

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
          Newest first · refreshes every 10 seconds
        </span>
      </div>

      {error && (
        <Alert variant="destructive">
          <AlertDescription>{error}</AlertDescription>
        </Alert>
      )}

      <div className="rounded-lg border">
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
                <TableHead>Node</TableHead>
                {canManage && onRetry && (
                  <TableHead>
                    <span className="sr-only">Actions</span>
                  </TableHead>
                )}
              </TableRow>
            </TableHeader>
            <TableBody>
              {records.map(r => (
                <TableRow key={r.id} className="align-top">
                  <TableCell className="max-w-md whitespace-normal">
                    <button
                      type="button"
                      className="text-left font-medium hover:underline"
                      onClick={() => onOpenJob(r.jobGroup, r.jobName)}
                    >
                      {r.jobName}
                    </button>
                    <div className="text-xs text-muted-foreground">
                      {r.jobGroup} · {shortType(r.jobType)}
                      {r.refireCount > 0 && ` · refire ${r.refireCount}`}
                      {r.attempt > 1 && ` · attempt ${r.attempt}`}
                    </div>
                    {r.error && (
                      <p
                        className="mt-1 line-clamp-2 text-xs break-words text-destructive"
                        title={r.error}
                      >
                        {r.error}
                      </p>
                    )}
                  </TableCell>
                  <TableCell>
                    <ExecutionStatusBadge status={r.status} />
                  </TableCell>
                  <TableCell title={formatDateTime(r.firedTimeUtc)}>
                    {formatRelative(r.firedTimeUtc)}
                  </TableCell>
                  <TableCell className="text-right tabular-nums">
                    {formatDuration(r.durationMs)}
                  </TableCell>
                  <TableCell className="font-mono text-xs text-muted-foreground">
                    {r.instanceName}
                  </TableCell>
                  {canManage && onRetry && (
                    <TableCell className="text-right">
                      {retryable.includes(r.status) && (
                        <Button
                          size="sm"
                          variant="outline"
                          aria-label={`Run ${r.jobName} again`}
                          onClick={async () => {
                            if (await onRetry(r)) setTimeout(refresh, 1000)
                          }}
                        >
                          <RotateCcw /> Run again
                        </Button>
                      )}
                    </TableCell>
                  )}
                </TableRow>
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
