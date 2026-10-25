import { useState, type FormEvent, type ReactNode } from 'react'
import { History, ListRestart, Lock, Pause, Pencil, Play, PlayCircle, Trash2 } from 'lucide-react'
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert'
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
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Separator } from '@/components/ui/separator'
import {
  Sheet,
  SheetContent,
  SheetDescription,
  SheetFooter,
  SheetHeader,
  SheetTitle,
} from '@/components/ui/sheet'
import { DataEditor } from '@/components/jobs/data-editor'
import {
  ExecutionStatusBadge,
  StatusBadge,
  TriggerStateBadge,
} from '@/components/jobs/status-badge'
import { RetryEditor } from '@/components/jobs/retry-editor'
import { TriggersEditor } from '@/components/jobs/triggers-editor'
import {
  changedData,
  dataRows,
  draftsFromTriggers,
  draftsToRequest,
  draftToRetry,
  retryDraft,
  rowsToData,
} from '@/lib/job-form'
import type { DashboardActions } from '@/hooks/use-dashboard'
import { useHistory } from '@/hooks/use-history'
import {
  describeMisfire,
  describeRetry,
  errorTone,
  formatDateTime,
  formatDuration,
  formatRelative,
  isPaused,
  recordTime,
  shortType,
  startedLate,
} from '@/lib/format'
import { cn } from '@/lib/utils'
import type { Job, JobType, Trigger, UpdateJobRequest } from '@/types'

interface Props {
  job: Job | null
  jobType: JobType | undefined
  canManage: boolean
  historyEnabled: boolean
  showScheduleAssistant: boolean
  secretReferences: boolean
  actions: DashboardActions
  onClose: () => void
  onViewHistory: (group: string, name: string) => void
}

export function JobSheet({
  job,
  jobType,
  canManage,
  historyEnabled,
  showScheduleAssistant,
  secretReferences,
  actions,
  onClose,
  onViewHistory,
}: Props) {
  // Editing the job, or choosing the data for one run
  const [mode, setMode] = useState<'view' | 'edit' | 'run'>('view')

  return (
    <Sheet
      open={job !== null}
      onOpenChange={open => {
        if (!open) {
          setMode('view')
          onClose()
        }
      }}
    >
      <SheetContent className="w-full gap-0 data-[side=right]:w-full data-[side=right]:sm:max-w-xl">
        {job && (
          <>
            <SheetHeader className="border-b">
              <div className="flex items-center gap-2 pr-8">
                <SheetTitle className="truncate">{job.name}</SheetTitle>
                <StatusBadge job={job} />
              </div>
              <SheetDescription>
                {job.group} ·{' '}
                {job.loadable ? (job.displayName ?? shortType(job.jobType)) : 'type not loaded'}
              </SheetDescription>
              {canManage && job.loadable && mode === 'view' && (
                <JobActions
                  job={job}
                  actions={actions}
                  onEdit={() => setMode('edit')}
                  onRunWithData={() => setMode('run')}
                  onDeleted={onClose}
                />
              )}
            </SheetHeader>
            {mode === 'edit' ? (
              <EditJobForm
                key={`${job.group}.${job.name}`}
                job={job}
                showScheduleAssistant={showScheduleAssistant}
                secretReferences={secretReferences}
                jobType={jobType}
                onCancel={() => setMode('view')}
                onSave={async request => {
                  if (await actions.update(job, request)) setMode('view')
                }}
              />
            ) : mode === 'run' ? (
              <RunWithDataForm
                key={`${job.group}.${job.name}`}
                job={job}
                jobType={jobType}
                secretReferences={secretReferences}
                onCancel={() => setMode('view')}
                onRun={async data => {
                  if (await actions.runWithData(job, data)) setMode('view')
                }}
              />
            ) : (
              <JobDetails
                job={job}
                onCancelRetry={
                  canManage && job.loadable
                    ? trigger => actions.cancelRetry(job, trigger)
                    : undefined
                }
              >
                {historyEnabled && (
                  <RecentRuns
                    key={`${job.group}.${job.name}`}
                    group={job.group}
                    name={job.name}
                    onViewAll={() => onViewHistory(job.group, job.name)}
                  />
                )}
              </JobDetails>
            )}
          </>
        )}
      </SheetContent>
    </Sheet>
  )
}

function JobActions({
  job,
  actions,
  onEdit,
  onRunWithData,
  onDeleted,
}: {
  job: Job
  actions: DashboardActions
  onEdit: () => void
  onRunWithData: () => void
  onDeleted: () => void
}) {
  const paused = isPaused(job)
  return (
    <div className="flex flex-wrap gap-2 pt-2">
      <Button size="sm" onClick={() => actions.run(job, 'trigger')}>
        <Play /> Run now
      </Button>
      <Button size="sm" variant="outline" onClick={onRunWithData}>
        <ListRestart /> Run with different data
      </Button>
      {paused ? (
        <Button size="sm" variant="outline" onClick={() => actions.run(job, 'resume')}>
          <PlayCircle /> Resume
        </Button>
      ) : (
        <Button
          size="sm"
          variant="outline"
          onClick={() => actions.run(job, 'pause')}
          disabled={job.triggers.length === 0}
        >
          <Pause /> Pause
        </Button>
      )}
      <Button size="sm" variant="outline" onClick={onEdit}>
        <Pencil /> Edit
      </Button>
      <AlertDialog>
        <AlertDialogTrigger asChild>
          <Button size="sm" variant="destructive" className="ml-auto">
            <Trash2 /> Delete
          </Button>
        </AlertDialogTrigger>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Delete {job.name}?</AlertDialogTitle>
            <AlertDialogDescription>
              The job {job.group}.{job.name} and all {job.triggers.length} of its triggers are
              removed from the scheduler. This cannot be undone.
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>Cancel</AlertDialogCancel>
            <AlertDialogAction
              variant="destructive"
              onClick={async () => {
                if (await actions.remove(job)) onDeleted()
              }}
            >
              Delete job
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </div>
  )
}

function JobDetails({
  job,
  onCancelRetry,
  children,
}: {
  job: Job
  onCancelRetry?: (trigger: Trigger) => void
  children?: ReactNode
}) {
  const data = Object.entries(job.data)

  return (
    <div className="flex-1 space-y-5 overflow-y-auto p-4">
      {!job.loadable && (
        <Alert variant="destructive">
          <Lock />
          <AlertTitle>Read-only in this dashboard</AlertTitle>
          <AlertDescription>
            <p>
              This job&apos;s type cannot be loaded in this application. It most likely belongs to
              another app that shares the scheduler.
            </p>
            <p className="font-mono text-xs">{job.loadError}</p>
          </AlertDescription>
        </Alert>
      )}

      <dl className="grid grid-cols-2 gap-x-4 gap-y-3 text-sm">
        <Field label="Type">
          <span title={job.jobType ?? undefined}>{job.displayName ?? shortType(job.jobType)}</span>
        </Field>
        <Field label="Durable">{job.durable ? 'Yes' : 'No'}</Field>
        {job.loadable && (
          <div className="col-span-2">
            <Field label="Retries">{describeRetry(job.retry)}</Field>
          </div>
        )}
        {job.description && (
          <div className="col-span-2">
            <Field label="Description">{job.description}</Field>
          </div>
        )}
      </dl>

      {children}

      <Separator />
      <section className="space-y-2">
        <h3 className="text-sm font-medium">Triggers ({job.triggers.length})</h3>
        {job.triggers.length === 0 ? (
          <p className="text-sm text-muted-foreground">
            No triggers. This job only runs when triggered manually.
          </p>
        ) : (
          <div className="space-y-2">
            {job.triggers.map(t => (
              <div key={`${t.group}.${t.name}`} className="space-y-2 rounded-lg border p-3 text-sm">
                <div className="flex items-center gap-2">
                  <span className="truncate font-medium">
                    {t.kind === 'Retry' && t.attempt ? `Retry, attempt ${t.attempt}` : t.name}
                  </span>
                  <TriggerStateBadge state={t.state} />
                  <Badge variant="outline" className="ml-auto">
                    {t.kind}
                  </Badge>
                  {t.kind === 'Retry' && onCancelRetry && (
                    <Button
                      size="sm"
                      variant="outline"
                      aria-label={`Cancel retry ${t.name}`}
                      onClick={() => onCancelRetry(t)}
                    >
                      Cancel
                    </Button>
                  )}
                </div>
                {t.cronExpression && (
                  <code className="block rounded bg-muted px-2 py-1 font-mono text-xs">
                    {t.cronExpression}
                  </code>
                )}
                <dl className="grid grid-cols-2 gap-x-4 gap-y-2">
                  <Field label="Next fire">
                    <FireTime iso={t.nextFireTimeUtc} empty="Not scheduled" />
                  </Field>
                  <Field label="Previous fire">
                    <FireTime iso={t.previousFireTimeUtc} empty="Never" />
                  </Field>
                  {t.timeZone && <Field label="Time zone">{t.timeZone}</Field>}
                  {describeMisfire(t.misfireInstruction) && (
                    <Field label="If a fire time is missed">
                      <span title={t.misfireInstruction}>
                        {describeMisfire(t.misfireInstruction)}
                      </span>
                    </Field>
                  )}
                </dl>
              </div>
            ))}
          </div>
        )}
      </section>

      {data.length > 0 && (
        <>
          <Separator />
          <section className="space-y-2">
            <h3 className="text-sm font-medium">Job data</h3>
            <dl className="divide-y rounded-lg border text-sm">
              {data.map(([key, value]) => (
                <div key={key} className="flex gap-4 px-3 py-2">
                  <dt
                    className="w-2/5 shrink-0 truncate font-mono text-xs text-muted-foreground"
                    title={key}
                  >
                    {key}
                  </dt>
                  <dd className="min-w-0 break-words">{value || '-'}</dd>
                </div>
              ))}
            </dl>
          </section>
        </>
      )}
    </div>
  )
}

function EditJobForm({
  job,
  jobType,
  showScheduleAssistant,
  secretReferences,
  onSave,
  onCancel,
}: {
  job: Job
  jobType: JobType | undefined
  showScheduleAssistant: boolean
  secretReferences: boolean
  onSave: (request: UpdateJobRequest) => Promise<void>
  onCancel: () => void
}) {
  const [description, setDescription] = useState(job.description ?? '')
  const [data, setData] = useState(() => dataRows(jobType, job.data))
  const [triggers, setTriggers] = useState(() => draftsFromTriggers(job.triggers))
  const [retry, setRetry] = useState(() => retryDraft(job.retry))
  const [saving, setSaving] = useState(false)
  const otherTriggers = job.triggers.filter(t => t.kind !== 'Cron').length

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setSaving(true)
    await onSave({
      description,
      data: rowsToData(data),
      triggers: draftsToRequest(triggers),
      retry: draftToRetry(retry),
    })
    setSaving(false)
  }

  return (
    <form onSubmit={submit} className="flex min-h-0 flex-1 flex-col">
      <div className="flex-1 space-y-5 overflow-y-auto p-4">
        <div className="space-y-1.5">
          <Label htmlFor="edit-description">Description</Label>
          <Input
            id="edit-description"
            value={description}
            onChange={e => setDescription(e.target.value)}
          />
        </div>

        <Separator />
        <section className="space-y-2">
          <h3 className="text-sm font-medium">Cron triggers</h3>
          <TriggersEditor
            triggers={triggers}
            onChange={setTriggers}
            showScheduleAssistant={showScheduleAssistant}
          />
          {otherTriggers > 0 && (
            <p className="text-xs text-muted-foreground">
              {otherTriggers} non-cron trigger(s) are left unchanged.
            </p>
          )}
        </section>

        <Separator />
        <section className="space-y-2">
          <h3 className="text-sm font-medium">Retries</h3>
          <RetryEditor value={retry} onChange={setRetry} />
        </section>

        <Separator />
        <section className="space-y-2">
          <h3 className="text-sm font-medium">Job data</h3>
          <DataEditor rows={data} onChange={setData} secretReferences={secretReferences} />
        </section>
      </div>

      <SheetFooter className="flex-row justify-end border-t">
        <Button type="button" variant="outline" onClick={onCancel} disabled={saving}>
          Cancel
        </Button>
        <Button type="submit" disabled={saving}>
          {saving ? 'Saving…' : 'Save changes'}
        </Button>
      </SheetFooter>
    </form>
  )
}

/** Runs the job once with some of its data changed. The saved job is not changed. */
function RunWithDataForm({
  job,
  jobType,
  secretReferences,
  onRun,
  onCancel,
}: {
  job: Job
  jobType: JobType | undefined
  secretReferences: boolean
  onRun: (data: Record<string, string>) => Promise<void>
  onCancel: () => void
}) {
  const [data, setData] = useState(() => dataRows(jobType, job.data))
  const [running, setRunning] = useState(false)
  const changed = Object.keys(changedData(data, job.data)).length

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setRunning(true)
    await onRun(changedData(data, job.data))
    setRunning(false)
  }

  return (
    <form onSubmit={submit} className="flex min-h-0 flex-1 flex-col">
      <div className="flex-1 space-y-3 overflow-y-auto p-4">
        <h3 className="text-sm font-medium">Data for this run</h3>
        <p className="text-sm text-muted-foreground">
          Changes apply to this run and its retries only. The saved job keeps its data, and the
          audit log records what was changed.
        </p>
        <DataEditor rows={data} onChange={setData} secretReferences={secretReferences} />
      </div>

      <SheetFooter className="flex-row items-center justify-end border-t">
        <span className="mr-auto text-xs text-muted-foreground">
          {changed === 0 ? 'Nothing changed' : `${changed} changed`}
        </span>
        <Button type="button" variant="outline" onClick={onCancel} disabled={running}>
          Cancel
        </Button>
        <Button type="submit" disabled={running}>
          <Play /> {running ? 'Starting…' : 'Run now'}
        </Button>
      </SheetFooter>
    </form>
  )
}

function RecentRuns({
  group,
  name,
  onViewAll,
}: {
  group: string
  name: string
  onViewAll: () => void
}) {
  const { records, loaded, error } = useHistory({ status: 'all', job: { group, name } }, 5)

  return (
    <>
      <Separator />
      <section className="space-y-2">
        <div className="flex items-center justify-between">
          <h3 className="text-sm font-medium">Recent runs</h3>
          <Button variant="link" size="sm" className="h-auto p-0" onClick={onViewAll}>
            <History /> All runs
          </Button>
        </div>
        {error ? (
          <p className="text-sm text-destructive">{error}</p>
        ) : !loaded ? (
          <p className="text-sm text-muted-foreground">Loading…</p>
        ) : records.length === 0 ? (
          <p className="text-sm text-muted-foreground">No runs recorded yet.</p>
        ) : (
          <ul className="divide-y rounded-lg border text-sm">
            {records.map(r => (
              <li key={r.id} className="space-y-1 px-3 py-2">
                <div className="flex items-center gap-2">
                  <ExecutionStatusBadge status={r.status} />
                  <span title={formatDateTime(recordTime(r))}>
                    {r.status === 'Misfired' && 'missed '}
                    {formatRelative(recordTime(r))}
                  </span>
                  {r.attempt > 1 && (
                    <span className="text-xs text-muted-foreground">attempt {r.attempt}</span>
                  )}
                  {startedLate(r) && (
                    <span className="text-xs text-amber-700 dark:text-amber-400">
                      started {startedLate(r)} late
                    </span>
                  )}
                  <span className="ml-auto text-muted-foreground tabular-nums">
                    {formatDuration(r.durationMs)}
                  </span>
                </div>
                {r.error && (
                  <p
                    className={cn('line-clamp-2 text-xs break-words', errorTone(r))}
                    title={r.error}
                  >
                    {r.error}
                  </p>
                )}
              </li>
            ))}
          </ul>
        )}
      </section>
    </>
  )
}

function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="min-w-0 space-y-0.5">
      <dt className="text-xs text-muted-foreground">{label}</dt>
      <dd className="truncate">{children}</dd>
    </div>
  )
}

function FireTime({ iso, empty }: { iso: string | null; empty: string }) {
  if (!iso) return <span className="text-muted-foreground">{empty}</span>
  return <span title={formatDateTime(iso)}>{formatRelative(iso)}</span>
}
