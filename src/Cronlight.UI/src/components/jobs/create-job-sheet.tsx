import { useState, type FormEvent } from 'react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { Separator } from '@/components/ui/separator'
import {
  Sheet,
  SheetContent,
  SheetDescription,
  SheetFooter,
  SheetHeader,
  SheetTitle,
} from '@/components/ui/sheet'
import { useDirtyReport, useDiscardGuard } from '@/components/discard-changes'
import { DataEditor } from '@/components/jobs/data-editor'
import { RetryEditor } from '@/components/jobs/retry-editor'
import { TriggersEditor } from '@/components/jobs/triggers-editor'
import {
  dataRows,
  draftsToRequest,
  draftToRetry,
  emptyTrigger,
  retryDraft,
  rowsToData,
} from '@/lib/job-form'
import type { CreateJobRequest, JobType } from '@/types'

interface Props {
  open: boolean
  onOpenChange: (open: boolean) => void
  jobTypes: JobType[]
  secretReferences: boolean
  onCreate: (request: CreateJobRequest) => Promise<boolean>
}

export function CreateJobSheet({
  open,
  onOpenChange,
  jobTypes,
  secretReferences,
  onCreate,
}: Props) {
  const guard = useDiscardGuard(() => onOpenChange(false), 'The job is not created.')
  return (
    <Sheet open={open} onOpenChange={guard.onOpenChange}>
      <SheetContent className="w-full gap-0 data-[side=right]:w-full data-[side=right]:sm:max-w-xl">
        {guard.dialog}
        <SheetHeader className="border-b">
          <SheetTitle>New job</SheetTitle>
          <SheetDescription>
            Schedule one of the job types found in this application.
          </SheetDescription>
        </SheetHeader>
        {/* Remount on open so the form always starts empty */}
        {open && (
          <CreateJobForm
            jobTypes={jobTypes}
            secretReferences={secretReferences}
            onCreate={onCreate}
            onDirtyChange={guard.setDirty}
            onDone={() => onOpenChange(false)}
          />
        )}
      </SheetContent>
    </Sheet>
  )
}

function CreateJobForm({
  jobTypes,
  secretReferences,
  onCreate,
  onDirtyChange,
  onDone,
}: Omit<Props, 'open' | 'onOpenChange'> & {
  /** Told whether anything was entered, so closing can ask first */
  onDirtyChange: (dirty: boolean) => void
  onDone: () => void
}) {
  const [typeName, setTypeName] = useState(jobTypes[0]?.typeName ?? '')
  const [name, setName] = useState('')
  const [group, setGroup] = useState('')
  const [description, setDescription] = useState('')
  const [data, setData] = useState(() => dataRows(jobTypes[0], {}))
  const [triggers, setTriggers] = useState([emptyTrigger()])
  const [retry, setRetry] = useState(() => retryDraft(jobTypes[0]?.retry))
  const [submitting, setSubmitting] = useState(false)
  const [cronInvalid, setCronInvalid] = useState(false)
  const jobType = jobTypes.find(t => t.typeName === typeName)

  // The form as entered, against the form as it opened
  const entered = JSON.stringify([
    typeName,
    name,
    group,
    description,
    rowsToData(data),
    draftsToRequest(triggers),
    draftToRetry(retry),
  ])
  const [opened] = useState(entered)
  useDirtyReport(entered !== opened, onDirtyChange)

  const changeType = (value: string) => {
    const type = jobTypes.find(t => t.typeName === value)
    setTypeName(value)
    setData(dataRows(type, {}))
    setRetry(retryDraft(type?.retry))
  }

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setSubmitting(true)
    const created = await onCreate({
      type: typeName,
      name: name.trim(),
      group: group.trim() || null,
      description: description.trim() || null,
      // An optional field left empty is left out, rather than saved as an empty value
      data: rowsToData(data.filter(row => row.value !== '')),
      triggers: draftsToRequest(triggers),
      retry: draftToRetry(retry),
    })
    setSubmitting(false)
    if (created) onDone()
  }

  return (
    <form onSubmit={submit} className="flex min-h-0 flex-1 flex-col">
      <div className="flex-1 space-y-5 overflow-y-auto p-4 [scrollbar-gutter:stable]">
        <div className="space-y-1.5">
          <Label htmlFor="job-type">Job type</Label>
          <Select value={typeName} onValueChange={changeType}>
            <SelectTrigger id="job-type" className="w-full">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {jobTypes.map(t => (
                <SelectItem key={t.typeName} value={t.typeName}>
                  {t.displayName}
                  <span className="text-xs text-muted-foreground">{t.typeName}</span>
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
          {jobType?.description && (
            <p className="text-xs text-muted-foreground">{jobType.description}</p>
          )}
        </div>

        <div className="grid gap-3 sm:grid-cols-2">
          <div className="space-y-1.5">
            <Label htmlFor="job-name">Name</Label>
            <Input
              id="job-name"
              value={name}
              onChange={e => setName(e.target.value)}
              placeholder="e.g. nightly-report"
              required
            />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="job-group">Group</Label>
            <Input
              id="job-group"
              value={group}
              onChange={e => setGroup(e.target.value)}
              placeholder="Empty: DEFAULT"
            />
          </div>
        </div>

        <div className="space-y-1.5">
          <Label htmlFor="job-description">Description</Label>
          <Input
            id="job-description"
            value={description}
            onChange={e => setDescription(e.target.value)}
            placeholder="Optional: what it does, for whoever reads this next"
          />
        </div>

        <Separator />
        <section className="space-y-2">
          <h3 className="text-base font-semibold">Triggers</h3>
          <TriggersEditor
            triggers={triggers}
            onChange={setTriggers}
            onInvalidChange={setCronInvalid}
          />
        </section>

        <Separator />
        <section className="space-y-2">
          <h3 className="text-base font-semibold">When a run fails</h3>
          <RetryEditor value={retry} onChange={setRetry} />
        </section>

        <Separator />
        <section className="space-y-2">
          <h3 className="text-base font-semibold">Job data</h3>
          <DataEditor rows={data} onChange={setData} secretReferences={secretReferences} />
        </section>
      </div>

      <SheetFooter className="flex-row justify-end border-t">
        <Button type="button" variant="outline" onClick={onDone}>
          Cancel
        </Button>
        <Button type="submit" disabled={submitting || !typeName || cronInvalid}>
          {submitting ? 'Creating…' : 'Create job'}
        </Button>
      </SheetFooter>
    </form>
  )
}
