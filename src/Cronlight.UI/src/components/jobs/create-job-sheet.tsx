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
import { DataEditor } from '@/components/jobs/data-editor'
import { TriggersEditor } from '@/components/jobs/triggers-editor'
import { dataRows, draftsToRequest, emptyTrigger, rowsToData } from '@/lib/job-form'
import type { CreateJobRequest, JobType } from '@/types'

interface Props {
  open: boolean
  onOpenChange: (open: boolean) => void
  jobTypes: JobType[]
  onCreate: (request: CreateJobRequest) => Promise<boolean>
}

export function CreateJobSheet({ open, onOpenChange, jobTypes, onCreate }: Props) {
  return (
    <Sheet open={open} onOpenChange={onOpenChange}>
      <SheetContent className="w-full gap-0 data-[side=right]:w-full data-[side=right]:sm:max-w-xl">
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
            onCreate={onCreate}
            onDone={() => onOpenChange(false)}
          />
        )}
      </SheetContent>
    </Sheet>
  )
}

function CreateJobForm({
  jobTypes,
  onCreate,
  onDone,
}: Omit<Props, 'open' | 'onOpenChange'> & { onDone: () => void }) {
  const [typeName, setTypeName] = useState(jobTypes[0]?.typeName ?? '')
  const [name, setName] = useState('')
  const [group, setGroup] = useState('')
  const [description, setDescription] = useState('')
  const [data, setData] = useState(() => dataRows(jobTypes[0], {}))
  const [triggers, setTriggers] = useState([emptyTrigger()])
  const [submitting, setSubmitting] = useState(false)
  const jobType = jobTypes.find(t => t.typeName === typeName)

  const changeType = (value: string) => {
    setTypeName(value)
    setData(
      dataRows(
        jobTypes.find(t => t.typeName === value),
        {}
      )
    )
  }

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setSubmitting(true)
    const created = await onCreate({
      type: typeName,
      name: name.trim(),
      group: group.trim() || null,
      description: description.trim() || null,
      data: rowsToData(data),
      triggers: draftsToRequest(triggers),
    })
    setSubmitting(false)
    if (created) onDone()
  }

  return (
    <form onSubmit={submit} className="flex min-h-0 flex-1 flex-col">
      <div className="flex-1 space-y-5 overflow-y-auto p-4">
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
              placeholder="nightly-report"
              required
            />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="job-group">Group</Label>
            <Input
              id="job-group"
              value={group}
              onChange={e => setGroup(e.target.value)}
              placeholder="DEFAULT"
            />
          </div>
        </div>

        <div className="space-y-1.5">
          <Label htmlFor="job-description">Description</Label>
          <Input
            id="job-description"
            value={description}
            onChange={e => setDescription(e.target.value)}
          />
        </div>

        <Separator />
        <section className="space-y-2">
          <h3 className="text-sm font-medium">Triggers</h3>
          <TriggersEditor triggers={triggers} onChange={setTriggers} jobName={name} />
        </section>

        <Separator />
        <section className="space-y-2">
          <h3 className="text-sm font-medium">Job data</h3>
          <DataEditor rows={data} onChange={setData} />
        </section>
      </div>

      <SheetFooter className="flex-row justify-end border-t">
        <Button type="button" variant="outline" onClick={onDone}>
          Cancel
        </Button>
        <Button type="submit" disabled={submitting || !typeName}>
          {submitting ? 'Creating…' : 'Create job'}
        </Button>
      </SheetFooter>
    </form>
  )
}
