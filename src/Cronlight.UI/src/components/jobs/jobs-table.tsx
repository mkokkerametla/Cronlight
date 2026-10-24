import { useState } from 'react'
import { Inbox, MoreHorizontal, Pause, Play, PlayCircle, Plus, Search, X } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { Input } from '@/components/ui/input'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table'
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip'
import { StatusBadge } from '@/components/jobs/status-badge'
import type { DashboardActions } from '@/hooks/use-dashboard'
import {
  formatDateTime,
  formatRelative,
  isPaused,
  jobKey,
  jobStatus,
  nextFire,
  typeLabel,
  type JobStatus,
} from '@/lib/format'
import { cn } from '@/lib/utils'
import type { Job, JobAction } from '@/types'

const ALL = 'all'

interface Props {
  jobs: Job[]
  canManage: boolean
  canCreate: boolean
  statusFilter: JobStatus | typeof ALL
  onStatusFilterChange: (status: JobStatus | typeof ALL) => void
  selectedKey: string | null
  onSelect: (job: Job) => void
  onNewJob: () => void
  actions: DashboardActions
}

export function JobsTable({
  jobs,
  canManage,
  canCreate,
  statusFilter,
  onStatusFilterChange,
  selectedKey,
  onSelect,
  onNewJob,
  actions,
}: Props) {
  const [search, setSearch] = useState('')
  const [typeFilter, setTypeFilter] = useState<string>(ALL)
  const [checked, setChecked] = useState<Set<string>>(new Set())
  const [bulkBusy, setBulkBusy] = useState(false)

  const query = search.trim().toLowerCase()
  const visible = jobs.filter(
    job =>
      (!query ||
        job.name.toLowerCase().includes(query) ||
        job.group.toLowerCase().includes(query)) &&
      (typeFilter === ALL || typeLabel(job) === typeFilter) &&
      (statusFilter === ALL || jobStatus(job) === statusFilter)
  )
  const selectable = visible.filter(j => j.loadable)
  const checkedJobs = jobs.filter(j => checked.has(jobKey(j)))
  const filtered = query || typeFilter !== ALL || statusFilter !== ALL

  const toggle = (job: Job) =>
    setChecked(prev => {
      const next = new Set(prev)
      const key = jobKey(job)
      if (next.has(key)) next.delete(key)
      else next.add(key)
      return next
    })

  const runBulk = async (action: JobAction) => {
    setBulkBusy(true)
    await actions.bulk(action, checkedJobs)
    setChecked(new Set())
    setBulkBusy(false)
  }

  return (
    <div className="space-y-3">
      <div className="flex flex-wrap items-center gap-2">
        <div className="relative min-w-48 flex-1">
          <Search className="pointer-events-none absolute top-1/2 left-2.5 size-4 -translate-y-1/2 text-muted-foreground" />
          <Input
            value={search}
            onChange={e => setSearch(e.target.value)}
            placeholder="Search name or group…"
            className="pl-8"
            aria-label="Search jobs"
          />
        </div>
        <Select value={typeFilter} onValueChange={setTypeFilter}>
          <SelectTrigger className="w-40" aria-label="Filter by type">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value={ALL}>All types</SelectItem>
            {[...new Set(jobs.map(typeLabel))].sort().map(t => (
              <SelectItem key={t} value={t}>
                {t}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
        <Select
          value={statusFilter}
          onValueChange={v => onStatusFilterChange(v as JobStatus | typeof ALL)}
        >
          <SelectTrigger className="w-36" aria-label="Filter by status">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value={ALL}>All statuses</SelectItem>
            <SelectItem value="Active">Active</SelectItem>
            <SelectItem value="Paused">Paused</SelectItem>
            <SelectItem value="Unloadable">Unloadable</SelectItem>
          </SelectContent>
        </Select>
        {filtered && (
          <Button
            variant="ghost"
            size="sm"
            onClick={() => {
              setSearch('')
              setTypeFilter(ALL)
              onStatusFilterChange(ALL)
            }}
          >
            <X /> Clear
          </Button>
        )}
        {canManage && (
          <Tooltip>
            <TooltipTrigger asChild>
              {/* span keeps the tooltip working while the button is disabled */}
              <span className="ml-auto">
                <Button onClick={onNewJob} disabled={!canCreate}>
                  <Plus /> New job
                </Button>
              </span>
            </TooltipTrigger>
            {!canCreate && (
              <TooltipContent>
                No IJob implementations were found in this application
              </TooltipContent>
            )}
          </Tooltip>
        )}
      </div>

      {canManage && checked.size > 0 && (
        <div className="flex flex-wrap items-center gap-2 rounded-lg border bg-muted/50 px-3 py-2 text-sm">
          <span className="font-medium">{checked.size} selected</span>
          <Button size="sm" variant="outline" disabled={bulkBusy} onClick={() => runBulk('pause')}>
            <Pause /> Pause
          </Button>
          <Button size="sm" variant="outline" disabled={bulkBusy} onClick={() => runBulk('resume')}>
            <PlayCircle /> Resume
          </Button>
          <Button
            size="sm"
            variant="ghost"
            className="ml-auto"
            onClick={() => setChecked(new Set())}
          >
            Clear selection
          </Button>
        </div>
      )}

      <div className="rounded-lg border">
        {visible.length === 0 ? (
          <div className="flex flex-col items-center gap-2 py-14 text-center">
            <Inbox className="size-8 text-muted-foreground" />
            <p className="font-medium">No jobs found</p>
            <p className="text-sm text-muted-foreground">
              {filtered ? 'No jobs match these filters.' : 'This scheduler has no jobs yet.'}
            </p>
          </div>
        ) : (
          <Table>
            <TableHeader>
              <TableRow>
                {canManage && (
                  <TableHead className="w-10">
                    <Checkbox
                      aria-label="Select all"
                      checked={
                        selectable.length > 0 && selectable.every(j => checked.has(jobKey(j)))
                          ? true
                          : checked.size > 0
                            ? 'indeterminate'
                            : false
                      }
                      onCheckedChange={value =>
                        setChecked(value === true ? new Set(selectable.map(jobKey)) : new Set())
                      }
                    />
                  </TableHead>
                )}
                <TableHead>Job</TableHead>
                <TableHead>Type</TableHead>
                <TableHead>Status</TableHead>
                <TableHead>Next fire</TableHead>
                {canManage && <TableHead className="w-28 text-right">Actions</TableHead>}
              </TableRow>
            </TableHeader>
            <TableBody>
              {visible.map(job => {
                const key = jobKey(job)
                const next = nextFire(job)
                return (
                  <TableRow
                    key={key}
                    data-state={selectedKey === key ? 'selected' : undefined}
                    className={cn(
                      // Tint plus an accent bar: the default bg-muted is too faint to spot
                      'cursor-pointer data-[state=selected]:bg-primary/10 data-[state=selected]:shadow-[inset_3px_0_0_var(--primary)]',
                      jobStatus(job) === 'Paused' && 'text-muted-foreground'
                    )}
                    onClick={() => onSelect(job)}
                  >
                    {canManage && (
                      <TableCell onClick={e => e.stopPropagation()}>
                        <Checkbox
                          aria-label={`Select ${job.name}`}
                          checked={checked.has(key)}
                          disabled={!job.loadable}
                          onCheckedChange={() => toggle(job)}
                        />
                      </TableCell>
                    )}
                    <TableCell>
                      <div className="font-medium text-foreground">{job.name}</div>
                      <div className="text-xs text-muted-foreground">{job.group}</div>
                    </TableCell>
                    <TableCell>
                      <Badge
                        variant={job.loadable ? 'secondary' : 'outline'}
                        className={cn(!job.loadable && 'text-destructive')}
                        title={
                          job.loadable ? (job.jobType ?? undefined) : (job.loadError ?? undefined)
                        }
                      >
                        {typeLabel(job)}
                      </Badge>
                    </TableCell>
                    <TableCell>
                      <StatusBadge job={job} />
                    </TableCell>
                    <TableCell title={next ? formatDateTime(next) : undefined}>
                      {next ? formatRelative(next) : '-'}
                    </TableCell>
                    {canManage && (
                      <TableCell className="text-right" onClick={e => e.stopPropagation()}>
                        {job.loadable && (
                          <RowActions job={job} actions={actions} onOpen={() => onSelect(job)} />
                        )}
                      </TableCell>
                    )}
                  </TableRow>
                )
              })}
            </TableBody>
          </Table>
        )}
      </div>
    </div>
  )
}

function RowActions({
  job,
  actions,
  onOpen,
}: {
  job: Job
  actions: DashboardActions
  onOpen: () => void
}) {
  const paused = isPaused(job)
  return (
    <div className="flex justify-end gap-1">
      <Tooltip>
        <TooltipTrigger asChild>
          <Button
            variant="ghost"
            size="icon-sm"
            aria-label={`Run ${job.name} now`}
            onClick={() => actions.run(job, 'trigger')}
          >
            <Play />
          </Button>
        </TooltipTrigger>
        <TooltipContent>Run now</TooltipContent>
      </Tooltip>
      <Tooltip>
        <TooltipTrigger asChild>
          <Button
            variant="ghost"
            size="icon-sm"
            aria-label={`${paused ? 'Resume' : 'Pause'} ${job.name}`}
            disabled={job.triggers.length === 0}
            onClick={() => actions.run(job, paused ? 'resume' : 'pause')}
          >
            {paused ? <PlayCircle /> : <Pause />}
          </Button>
        </TooltipTrigger>
        <TooltipContent>{paused ? 'Resume' : 'Pause'}</TooltipContent>
      </Tooltip>
      <DropdownMenu>
        <DropdownMenuTrigger asChild>
          <Button variant="ghost" size="icon-sm" aria-label={`More actions for ${job.name}`}>
            <MoreHorizontal />
          </Button>
        </DropdownMenuTrigger>
        <DropdownMenuContent align="end">
          <DropdownMenuItem onSelect={onOpen}>Open details</DropdownMenuItem>
        </DropdownMenuContent>
      </DropdownMenu>
    </div>
  )
}
