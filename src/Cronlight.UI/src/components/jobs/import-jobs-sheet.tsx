import { useState, type ChangeEvent } from 'react'
import { AlertCircle, FileUp } from 'lucide-react'
import { toast } from 'sonner'
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Label } from '@/components/ui/label'
import {
  Sheet,
  SheetContent,
  SheetDescription,
  SheetFooter,
  SheetHeader,
  SheetTitle,
} from '@/components/ui/sheet'
import { Textarea } from '@/components/ui/textarea'
import { importJobs, type ImportOutcome } from '@/api'
import type { ImportedJob } from '@/types'

interface Props {
  open: boolean
  onOpenChange: (open: boolean) => void
  /** Called once jobs were imported, to show them */
  onImported: () => void
}

export function ImportJobsSheet({ open, onOpenChange, onImported }: Props) {
  return (
    <Sheet open={open} onOpenChange={onOpenChange}>
      <SheetContent className="w-full gap-0 data-[side=right]:w-full data-[side=right]:sm:max-w-xl">
        <SheetHeader className="border-b">
          <SheetTitle>Import jobs</SheetTitle>
          <SheetDescription>
            Creates the file’s jobs that this scheduler does not have, and updates the others to
            match it. Jobs the file does not list are left alone.
          </SheetDescription>
        </SheetHeader>
        {/* Remount on open so the form always starts empty */}
        {open && (
          <ImportForm
            onDone={imported => {
              onOpenChange(false)
              if (imported) onImported()
            }}
          />
        )}
      </SheetContent>
    </Sheet>
  )
}

/**
 * Sent as JSON when it starts with a brace or bracket, so a mistake in JSON is answered as JSON; the server reads what
 * is not JSON but YAML in flow style, which starts with a brace too, as YAML. Anything else is sent as YAML.
 */
const isYaml = (text: string) => !/^\s*[[{]/.test(text)

function ImportForm({ onDone }: { onDone: (imported: boolean) => void }) {
  const [text, setText] = useState('')
  // What checking the text answered; cleared when the text changes, so only a checked file is imported
  const [checked, setChecked] = useState<ImportOutcome | null>(null)
  const [busy, setBusy] = useState(false)

  const changeText = (value: string) => {
    setText(value)
    setChecked(null)
  }

  const readFile = async (e: ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0]
    if (!file) return
    changeText(await file.text())
    e.target.value = ''
  }

  const run = async (dryRun: boolean) => {
    setBusy(true)
    try {
      const outcome = await importJobs(text, isYaml(text), dryRun)
      if (dryRun || !outcome.ok) setChecked(outcome)
      else {
        const { created, updated } = outcome.result
        toast.success(`Imported: ${created} created, ${updated} updated`)
        onDone(true)
      }
    } catch (err) {
      toast.error((err as Error).message)
    } finally {
      setBusy(false)
    }
  }

  const changes = checked?.ok ? checked.result.created + checked.result.updated : 0

  return (
    <div className="flex min-h-0 flex-1 flex-col">
      <div className="flex-1 space-y-4 overflow-y-auto p-4 [scrollbar-gutter:stable]">
        <div className="space-y-1.5">
          <div className="flex items-center justify-between gap-2">
            <Label htmlFor="import-text">Exported JSON or YAML</Label>
            <Button variant="outline" size="sm" asChild>
              <label>
                <FileUp /> Choose file
                <input
                  type="file"
                  accept=".json,.yaml,.yml,application/json,application/yaml"
                  className="sr-only"
                  aria-label="Choose a file to import"
                  onChange={readFile}
                />
              </label>
            </Button>
          </div>
          <Textarea
            id="import-text"
            value={text}
            onChange={e => changeText(e.target.value)}
            placeholder={'version: 1\njobs:\n- name: nightly-report\n  type: …'}
            className="max-h-80 min-h-48 font-mono text-xs"
            spellCheck={false}
          />
        </div>

        {checked && !checked.ok && (
          <Alert variant="destructive">
            <AlertCircle />
            <AlertTitle>Nothing can be imported</AlertTitle>
            <AlertDescription>
              {checked.jobs.length > 0
                ? 'Fix the jobs below and check the file again: none is imported until every one can be.'
                : checked.message}
            </AlertDescription>
          </Alert>
        )}
        {checked?.ok && (
          <p className="text-sm" role="status">
            {changes === 0
              ? 'Every job in the file is already as it says: nothing to import.'
              : `${checked.result.created} to create, ${checked.result.updated} to update, ${checked.result.unchanged} unchanged.`}
          </p>
        )}
        {checked && (
          <ul className="space-y-2" aria-label="Jobs in the file">
            {(checked.ok ? checked.result.jobs : checked.jobs).map((job, i) => (
              <ImportedJobItem key={i} job={job} />
            ))}
          </ul>
        )}
      </div>

      <SheetFooter className="flex-row justify-end border-t">
        <Button type="button" variant="outline" onClick={() => onDone(false)}>
          Cancel
        </Button>
        {checked?.ok && changes > 0 ? (
          <Button disabled={busy} onClick={() => void run(false)}>
            {busy ? 'Importing…' : `Import ${changes} ${changes === 1 ? 'job' : 'jobs'}`}
          </Button>
        ) : (
          <Button disabled={busy || !text.trim()} onClick={() => void run(true)}>
            {busy ? 'Checking…' : 'Check file'}
          </Button>
        )}
      </SheetFooter>
    </div>
  )
}

const actionBadge: Record<
  ImportedJob['action'],
  { label: string; variant: 'default' | 'secondary' | 'outline' | 'destructive' }
> = {
  create: { label: 'New', variant: 'default' },
  update: { label: 'Changed', variant: 'secondary' },
  unchanged: { label: 'Unchanged', variant: 'outline' },
  error: { label: 'Error', variant: 'destructive' },
}

function ImportedJobItem({ job }: { job: ImportedJob }) {
  const badge = actionBadge[job.action]
  const fields = Object.entries(job.changes)
  return (
    <li className="rounded-lg border p-3 text-sm">
      <div className="flex items-center gap-2">
        <Badge variant={badge.variant}>{badge.label}</Badge>
        <span className="min-w-0 truncate font-medium">
          {job.group}.{job.name || '(no name)'}
        </span>
      </div>
      {job.error && <p className="mt-1.5 text-destructive">{job.error}</p>}
      {job.action === 'update' && fields.length > 0 && (
        <dl className="mt-1.5 space-y-1 text-xs">
          {fields.map(([field, change]) => (
            <div key={field} className="grid grid-cols-[8rem_1fr] gap-2">
              <dt className="truncate text-muted-foreground">{field}</dt>
              <dd className="min-w-0 break-words">
                <span className="text-muted-foreground line-through">
                  {change.before ?? '(none)'}
                </span>
                {' → '}
                {change.after ?? '(none)'}
              </dd>
            </div>
          ))}
        </dl>
      )}
    </li>
  )
}
