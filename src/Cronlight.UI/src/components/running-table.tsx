import { Coffee } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table'
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip'
import { formatDateTime, formatDuration, shortType } from '@/lib/format'
import type { RunningJobs } from '@/types'

export function RunningTable({ running }: { running: RunningJobs }) {
  return (
    <div className="space-y-3">
      <div className="flex items-center gap-2 text-sm text-muted-foreground">
        <Tooltip>
          <TooltipTrigger asChild>
            <Badge variant="outline">
              {running.source === 'cluster' ? 'All nodes' : 'This node only'}
            </Badge>
          </TooltipTrigger>
          <TooltipContent>
            {running.source === 'cluster'
              ? 'Read from the job store, so it includes every node in the cluster'
              : 'In-memory store: only jobs running on this node are visible'}
          </TooltipContent>
        </Tooltip>
        Refreshes every 10 seconds
      </div>

      <div className="rounded-lg border">
        {running.jobs.length === 0 ? (
          <div className="flex flex-col items-center gap-2 py-14 text-center">
            <Coffee className="size-8 text-muted-foreground" />
            <p className="font-medium">No jobs running</p>
            <p className="text-sm text-muted-foreground">Trigger a job to see it here.</p>
          </div>
        ) : (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Job</TableHead>
                <TableHead>Type</TableHead>
                <TableHead>Node</TableHead>
                <TableHead>Started</TableHead>
                <TableHead className="text-right">Running for</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {running.jobs.map(job => (
                <TableRow key={job.fireInstanceId}>
                  <TableCell>
                    <div className="font-medium">{job.name}</div>
                    <div className="text-xs text-muted-foreground">{job.group}</div>
                  </TableCell>
                  <TableCell>
                    <Badge variant="secondary">{shortType(job.jobType)}</Badge>
                  </TableCell>
                  <TableCell className="font-mono text-xs">{job.instanceName}</TableCell>
                  <TableCell>{formatDateTime(job.firedTimeUtc)}</TableCell>
                  <TableCell className="text-right tabular-nums">
                    <span className="inline-flex items-center gap-2">
                      <span className="size-2 animate-pulse rounded-full bg-emerald-500" />
                      {formatDuration(job.runningForMs)}
                    </span>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </div>
    </div>
  )
}
