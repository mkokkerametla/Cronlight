import { useEffect, useRef, useState } from 'react'
import {
  ChevronLeft,
  ChevronRight,
  Download,
  Inbox,
  MoreHorizontal,
  Pause,
  Play,
  PlayCircle,
  Plus,
  Search,
  Upload,
  X,
} from 'lucide-react'
import { toast } from 'sonner'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from '@/components/ui/alert-dialog'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { Input } from '@/components/ui/input'
import { Kbd } from '@/components/ui/kbd'
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
import { RecentRuns } from '@/components/jobs/recent-runs'
import { StatusBadge } from '@/components/jobs/status-badge'
import { exportJobs } from '@/api'
import type { DashboardActions } from '@/hooks/use-dashboard'
import {
  canResume,
  formatDateTime,
  jobKey,
  jobStatus,
  nextFire,
  nextFireTime,
  scheduleSummary,
  typeLabel,
} from '@/lib/format'
import { cn } from '@/lib/utils'
import type { BulkAction, Job, JobAction, JobPage, JobQuery, JobStatus } from '@/types'

const ALL = 'all'
// Typing waits this long before searching, so the server is asked once per pause rather than per key
const SEARCH_DELAY_MS = 300

interface Props {
  /** The current page, as the server searched, filtered and paged it */
  jobs: Job[]
  page: Pick<JobPage, 'total' | 'page' | 'pageSize' | 'types'> | null
  query: JobQuery
  onQueryChange: (query: JobQuery) => void
  canManage: boolean
  canCreate: boolean
  selectedKey: string | null
  onSelect: (job: Job) => void
  onNewJob: () => void
  onImport: () => void
  actions: DashboardActions
  /** Execution history is on: each job's last runs are shown */
  history?: boolean
}

// Phones keep the job, its status and its actions, which the next fire time's schedule would push off the screen; the
// job's panel has it. Selecting several jobs is for wider screens.
const fromSm = 'hidden sm:table-cell'
const fromMd = 'hidden md:table-cell'
const fromLg = 'hidden lg:table-cell'

export function JobsTable({
  jobs,
  page,
  query,
  onQueryChange,
  canManage,
  canCreate,
  selectedKey,
  onSelect,
  onNewJob,
  onImport,
  actions,
  history = false,
}: Props) {
  const [search, setSearch] = useState(query.search)
  const [checked, setChecked] = useState<Set<string>>(new Set())
  const [bulkBusy, setBulkBusy] = useState(false)
  const searchBox = useRef<HTMLInputElement>(null)
  // The row j and k have moved to, which Enter opens; none once the page no longer has it
  const [active, setActive] = useState<number | null>(null)
  const current = active !== null && active < jobs.length ? active : null

  // Keyboard: / to search, j and k to move through the jobs, Enter to open one, Esc to stop. Not while typing, or while
  // a panel, dialog or menu has the keyboard.
  const keys = useRef({ jobs, current, onSelect })
  useEffect(() => {
    keys.current = { jobs, current, onSelect }
  })
  useEffect(() => {
    const onKeyDown = (e: KeyboardEvent) => {
      if (
        e.defaultPrevented ||
        e.ctrlKey ||
        e.metaKey ||
        e.altKey ||
        typing(e.target) ||
        overlayOpen()
      )
        return
      const { jobs, current, onSelect } = keys.current
      if (e.key === '/') {
        e.preventDefault()
        searchBox.current?.focus()
      } else if ((e.key === 'j' || e.key === 'k') && jobs.length > 0) {
        e.preventDefault()
        const step = e.key === 'j' ? 1 : -1
        setActive(current === null ? 0 : Math.min(Math.max(current + step, 0), jobs.length - 1))
      } else if (e.key === 'Enter' && current !== null && e.target === document.body) {
        e.preventDefault()
        onSelect(jobs[current])
      } else if (e.key === 'Escape') {
        setActive(null)
      }
    }
    document.addEventListener('keydown', onKeyDown)
    return () => document.removeEventListener('keydown', onKeyDown)
  }, [])
  useEffect(() => {
    if (current !== null)
      document.querySelector('tr[data-active="true"]')?.scrollIntoView?.({ block: 'nearest' })
  }, [current])

  // Changing what is listed also clears the selection, so bulk actions only touch jobs on screen
  const changeQuery = (next: Partial<JobQuery>) => {
    setChecked(new Set())
    onQueryChange({ ...query, page: 1, ...next })
  }

  const latest = useRef({ query, changeQuery })
  useEffect(() => {
    latest.current = { query, changeQuery }
  })
  useEffect(() => {
    if (search === latest.current.query.search) return
    const timer = setTimeout(() => latest.current.changeQuery({ search }), SEARCH_DELAY_MS)
    return () => clearTimeout(timer)
  }, [search])

  const selectable = jobs.filter(j => j.loadable)
  // Only jobs on this page: the page may have changed under a selection made earlier
  const checkedJobs = jobs.filter(j => checked.has(jobKey(j)))
  const filtered = query.search || query.type || query.status

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
            ref={searchBox}
            value={search}
            onChange={e => setSearch(e.target.value)}
            placeholder="Search name or group…"
            className="pr-8 pl-8"
            aria-label="Search jobs"
          />
          <Kbd className="absolute top-1/2 right-2 -translate-y-1/2" aria-hidden>
            /
          </Kbd>
        </div>
        <Select
          value={query.type ?? ALL}
          onValueChange={v => changeQuery({ type: v === ALL ? null : v })}
        >
          <SelectTrigger className="w-40" aria-label="Filter by type">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value={ALL}>All types</SelectItem>
            {(page?.types ?? []).map(t => (
              <SelectItem key={t} value={t}>
                {t}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
        <Select
          value={query.status ?? ALL}
          onValueChange={v => changeQuery({ status: v === ALL ? null : (v as JobStatus) })}
        >
          <SelectTrigger className="w-36" aria-label="Filter by status">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value={ALL}>All statuses</SelectItem>
            <SelectItem value="Active">Active</SelectItem>
            <SelectItem value="Paused">Paused</SelectItem>
            <SelectItem value="Unloadable">Unloadable</SelectItem>
            <SelectItem value="Error">Error</SelectItem>
          </SelectContent>
        </Select>
        {filtered && (
          <Button
            variant="ghost"
            size="sm"
            onClick={() => {
              setSearch('')
              changeQuery({ search: '', type: null, status: null })
            }}
          >
            <X /> Clear
          </Button>
        )}
        <div className="ml-auto">
          <SchedulerActions actions={actions} canManage={canManage} onImport={onImport} />
        </div>
        {canManage && (
          <Tooltip>
            <TooltipTrigger asChild>
              {/* span keeps the tooltip working while the button is disabled */}
              <span>
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

      {canManage && checkedJobs.length > 0 && (
        <div className="hidden flex-wrap items-center gap-2 rounded-lg border bg-muted/50 px-3 py-2 text-sm sm:flex">
          <span className="font-medium">{checkedJobs.length} selected</span>
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
        {jobs.length === 0 ? (
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
                  <TableHead className={cn('w-10', fromSm)}>
                    <Checkbox
                      aria-label="Select all"
                      checked={
                        selectable.length > 0 && selectable.every(j => checked.has(jobKey(j)))
                          ? true
                          : checkedJobs.length > 0
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
                <TableHead className={fromMd}>Type</TableHead>
                <TableHead>Status</TableHead>
                {history && <TableHead className={fromLg}>Recent runs</TableHead>}
                <TableHead className={fromSm}>Next fire</TableHead>
                {canManage && <TableHead className="w-28 text-right">Actions</TableHead>}
              </TableRow>
            </TableHeader>
            <TableBody>
              {jobs.map((job, index) => {
                const key = jobKey(job)
                const next = nextFire(job)
                const schedule = scheduleSummary(job)
                return (
                  <TableRow
                    key={key}
                    data-state={selectedKey === key ? 'selected' : undefined}
                    data-active={current === index ? 'true' : undefined}
                    className={cn(
                      // Tint plus an accent bar: the default bg-muted is too faint to spot
                      'cursor-pointer data-[state=selected]:bg-primary/10 data-[state=selected]:shadow-[inset_3px_0_0_var(--primary)]',
                      // The row the keyboard is on
                      'data-[active=true]:bg-muted data-[active=true]:shadow-[inset_3px_0_0_var(--muted-foreground)]',
                      jobStatus(job) === 'Paused' && 'text-muted-foreground'
                    )}
                    onClick={() => onSelect(job)}
                  >
                    {canManage && (
                      <TableCell className={fromSm} onClick={e => e.stopPropagation()}>
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
                    <TableCell className={fromMd}>
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
                    {history && (
                      <TableCell className={fromLg}>
                        {job.recentRuns ? (
                          <RecentRuns runs={job.recentRuns} />
                        ) : (
                          // History could not be read: not the same as no runs yet
                          <span
                            className="text-muted-foreground"
                            title="History could not be read: see the History tab"
                          >
                            -
                          </span>
                        )}
                      </TableCell>
                    )}
                    <TableCell className={fromSm} title={next ? formatDateTime(next) : undefined}>
                      <div>{nextFireTime(next) ?? '-'}</div>
                      {schedule && <div className="text-xs text-muted-foreground">{schedule}</div>}
                    </TableCell>
                    {canManage && (
                      <TableCell className="text-right" onClick={e => e.stopPropagation()}>
                        {job.loadable && (
                          <RowActions
                            job={job}
                            group={jobs.filter(j => j.group === job.group && j.loadable)}
                            actions={actions}
                            onOpen={() => onSelect(job)}
                          />
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

      {page && page.total > page.pageSize && (
        <Pagination page={page} onPageChange={p => changeQuery({ page: p })} />
      )}
    </div>
  )
}

function Pagination({
  page,
  onPageChange,
}: {
  page: Pick<JobPage, 'total' | 'page' | 'pageSize'>
  onPageChange: (page: number) => void
}) {
  const first = (page.page - 1) * page.pageSize + 1
  const last = Math.min(page.page * page.pageSize, page.total)
  const pages = Math.ceil(page.total / page.pageSize)
  return (
    <nav className="flex items-center justify-end gap-2 text-sm" aria-label="Job list pages">
      <span className="text-muted-foreground tabular-nums">
        {first.toLocaleString()}–{last.toLocaleString()} of {page.total.toLocaleString()}
      </span>
      <Button
        variant="outline"
        size="sm"
        disabled={page.page <= 1}
        onClick={() => onPageChange(page.page - 1)}
      >
        <ChevronLeft /> Previous
      </Button>
      <Button
        variant="outline"
        size="sm"
        disabled={page.page >= pages}
        onClick={() => onPageChange(page.page + 1)}
      >
        Next <ChevronRight />
      </Button>
    </nav>
  )
}

function RowActions({
  job,
  group,
  actions,
  onOpen,
}: {
  job: Job
  /** The jobs of its group on this page, which say whether pausing or resuming the group would change anything */
  group: Job[]
  actions: DashboardActions
  onOpen: () => void
}) {
  // A job Quartz stopped is resumed too
  const paused = canResume(job)
  const groupRuns = group.some(j => !canResume(j))
  const groupWaits = group.some(canResume)
  const [confirming, setConfirming] = useState<BulkTarget | null>(null)
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
          <DropdownMenuSeparator />
          {groupRuns && (
            <DropdownMenuItem onSelect={() => setConfirming({ group: job.group, action: 'pause' })}>
              <Pause /> Pause group {job.group}
            </DropdownMenuItem>
          )}
          {groupWaits && (
            <DropdownMenuItem
              onSelect={() => setConfirming({ group: job.group, action: 'resume' })}
            >
              <PlayCircle /> Resume group {job.group}
            </DropdownMenuItem>
          )}
        </DropdownMenuContent>
      </DropdownMenu>
      <ConfirmBulk target={confirming} onClose={() => setConfirming(null)} actions={actions} />
    </div>
  )
}

/** Every job in a group, or with a null group every job in the scheduler */
interface BulkTarget {
  group: string | null
  action: BulkAction
}

function bulkText({ group, action }: BulkTarget) {
  if (group === null)
    return action === 'pause'
      ? {
          title: 'Pause every job?',
          description:
            'No job in this scheduler runs on schedule until it is resumed, on any node, and jobs added meanwhile start paused too. Jobs can still be run by hand.',
          confirm: 'Pause all jobs',
        }
      : {
          title: 'Resume every job?',
          description:
            'Every paused job in this scheduler runs on schedule again, including jobs that were paused one by one.',
          confirm: 'Resume all jobs',
        }
  return action === 'pause'
    ? {
        title: `Pause group ${group}?`,
        description: `No job in group ${group} runs on schedule until the group is resumed, on any node. Jobs can still be run by hand. With the in-memory job store, jobs added to the group meanwhile start paused too.`,
        confirm: 'Pause group',
      }
    : {
        title: `Resume group ${group}?`,
        description: `Every paused job in group ${group} runs on schedule again, including jobs that were paused one by one.`,
        confirm: 'Resume group',
      }
}

/** Asks before pausing or resuming many jobs at once: it reaches every node, and resuming undoes pauses of single jobs */
function ConfirmBulk({
  target,
  onClose,
  actions,
}: {
  target: BulkTarget | null
  onClose: () => void
  actions: DashboardActions
}) {
  const text = target && bulkText(target)
  return (
    <AlertDialog open={target !== null} onOpenChange={open => !open && onClose()}>
      <AlertDialogContent>
        <AlertDialogHeader>
          <AlertDialogTitle>{text?.title}</AlertDialogTitle>
          <AlertDialogDescription>{text?.description}</AlertDialogDescription>
        </AlertDialogHeader>
        <AlertDialogFooter>
          <AlertDialogCancel>Cancel</AlertDialogCancel>
          <AlertDialogAction
            onClick={() => {
              if (!target) return
              void (target.group === null
                ? actions.all(target.action)
                : actions.group(target.group, target.action))
            }}
          >
            {text?.confirm}
          </AlertDialogAction>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  )
}

/** Exports every job to a file; for managers also imports a file, and pauses or resumes every job, after asking */
function SchedulerActions({
  actions,
  canManage,
  onImport,
}: {
  actions: DashboardActions
  canManage: boolean
  onImport: () => void
}) {
  const [confirming, setConfirming] = useState<BulkTarget | null>(null)
  const download = (format: 'json' | 'yaml') =>
    exportJobs(format)
      .then(({ notExported }) => {
        if (notExported > 0)
          toast.warning(
            `${notExported} ${notExported === 1 ? 'job was' : 'jobs were'} not exported`,
            {
              description:
                'Their classes cannot be loaded in this app: they belong to another app sharing the database. Export them from that app.',
            }
          )
      })
      .catch((err: Error) => toast.error('Could not export the jobs', { description: err.message }))
  return (
    <>
      <DropdownMenu>
        <DropdownMenuTrigger asChild>
          <Button variant="outline" aria-label="All jobs">
            <MoreHorizontal /> All jobs
          </Button>
        </DropdownMenuTrigger>
        <DropdownMenuContent align="end" className="w-auto">
          <DropdownMenuItem onSelect={() => void download('yaml')}>
            <Download /> Export as YAML
          </DropdownMenuItem>
          <DropdownMenuItem onSelect={() => void download('json')}>
            <Download /> Export as JSON
          </DropdownMenuItem>
          {canManage && (
            <>
              <DropdownMenuItem onSelect={onImport}>
                <Upload /> Import jobs…
              </DropdownMenuItem>
              <DropdownMenuSeparator />
              <DropdownMenuItem onSelect={() => setConfirming({ group: null, action: 'pause' })}>
                <Pause /> Pause all jobs
              </DropdownMenuItem>
              <DropdownMenuItem onSelect={() => setConfirming({ group: null, action: 'resume' })}>
                <PlayCircle /> Resume all jobs
              </DropdownMenuItem>
            </>
          )}
        </DropdownMenuContent>
      </DropdownMenu>
      <ConfirmBulk target={confirming} onClose={() => setConfirming(null)} actions={actions} />
    </>
  )
}

/** The key goes to a field being typed in, not to a shortcut */
const typing = (target: EventTarget | null) =>
  target instanceof HTMLElement &&
  (target.isContentEditable ||
    ['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName) ||
    target.closest('[role="combobox"], [role="listbox"], [role="menu"]') !== null)

/** A panel, dialog or menu is open, and has the keyboard */
const overlayOpen = () =>
  document.querySelector('[role="dialog"], [role="alertdialog"], [role="menu"]') !== null
