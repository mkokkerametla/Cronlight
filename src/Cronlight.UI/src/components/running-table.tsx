import { Coffee, OctagonX } from 'lucide-react'
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
  AlertDialogTrigger,
} from '@/components/ui/alert-dialog'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
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
import type { RunningJob, RunningJobs } from '@/types'

export function RunningTable({
  running,
  canManage = false,
  localInstanceId,
  onCancel,
}: {
  running: RunningJobs
  canManage?: boolean
  /** This node's scheduler instance id: Quartz can only cancel runs on the node serving the dashboard */
  localInstanceId?: string
  onCancel?: (job: RunningJob) => Promise<boolean>
}) {
  const showCancel = canManage && onCancel != null
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
                {showCancel && <TableHead className="w-0" />}
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
                  {showCancel && (
                    <TableCell>
                      <CancelRun
                        job={job}
                        local={running.source === 'local' || job.instanceName === localInstanceId}
                        onCancel={onCancel}
                      />
                    </TableCell>
                  )}
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </div>
    </div>
  )
}

function CancelRun({
  job,
  local,
  onCancel,
}: {
  job: RunningJob
  local: boolean
  onCancel: (job: RunningJob) => Promise<boolean>
}) {
  if (!local)
    return (
      <Tooltip>
        <TooltipTrigger asChild>
          {/* A span, since a disabled button gets no pointer events for the tooltip */}
          <span tabIndex={0}>
            <Button size="sm" variant="ghost" disabled aria-label={`Cancel ${job.name}`}>
              <OctagonX /> Cancel
            </Button>
          </span>
        </TooltipTrigger>
        <TooltipContent>
          Runs on {job.instanceName}. Only that node can cancel it: open the dashboard there.
        </TooltipContent>
      </Tooltip>
    )

  return (
    <AlertDialog>
      <AlertDialogTrigger asChild>
        <Button size="sm" variant="ghost" aria-label={`Cancel ${job.name}`}>
          <OctagonX /> Cancel
        </Button>
      </AlertDialogTrigger>
      <AlertDialogContent>
        <AlertDialogHeader>
          <AlertDialogTitle>Cancel this run of {job.name}?</AlertDialogTitle>
          <AlertDialogDescription>
            The job is asked to stop. It stops when it next checks for cancellation; a job that
            never checks keeps running until it finishes.
          </AlertDialogDescription>
        </AlertDialogHeader>
        <AlertDialogFooter>
          <AlertDialogCancel>Keep running</AlertDialogCancel>
          <AlertDialogAction variant="destructive" onClick={() => void onCancel(job)}>
            Cancel run
          </AlertDialogAction>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  )
}
