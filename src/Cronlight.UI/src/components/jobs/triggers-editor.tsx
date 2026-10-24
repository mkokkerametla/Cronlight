import { Plus, X } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { CronPreview } from '@/components/jobs/cron-preview'
import { emptyTrigger, type TriggerDraft } from '@/lib/job-form'

interface Props {
  triggers: TriggerDraft[]
  onChange: (triggers: TriggerDraft[]) => void
  jobName: string
}

export function TriggersEditor({ triggers, onChange, jobName }: Props) {
  const update = (index: number, patch: Partial<TriggerDraft>) =>
    onChange(triggers.map((t, i) => (i === index ? { ...t, ...patch } : t)))

  return (
    <div className="space-y-3">
      {triggers.map((trigger, index) => (
        <div key={index} className="space-y-1.5 rounded-md border p-2.5">
          <div className="flex flex-wrap gap-2">
            <Input
              className="min-w-0 flex-[2_1_14rem] font-mono"
              value={trigger.cronExpression}
              onChange={e => update(index, { cronExpression: e.target.value })}
              placeholder="0 0/5 * * * ?"
              aria-label="Cron expression"
            />
            <Input
              className="min-w-0 flex-[1_1_9rem]"
              value={trigger.timeZone}
              onChange={e => update(index, { timeZone: e.target.value })}
              placeholder="UTC"
              aria-label="Time zone"
            />
            <Input
              className="min-w-0 flex-[1_1_9rem]"
              value={trigger.name}
              onChange={e => update(index, { name: e.target.value })}
              placeholder={`${jobName || 'job'}-trigger-${index + 1}`}
              aria-label="Trigger name"
            />
            <Button
              type="button"
              variant="ghost"
              size="icon"
              onClick={() => onChange(triggers.filter((_, i) => i !== index))}
              aria-label="Remove trigger"
            >
              <X />
            </Button>
          </div>
          <CronPreview expression={trigger.cronExpression} timeZone={trigger.timeZone} />
        </div>
      ))}
      <Button
        type="button"
        variant="outline"
        size="sm"
        onClick={() => onChange([...triggers, emptyTrigger()])}
      >
        <Plus /> Add trigger
      </Button>
      <p className="text-xs text-muted-foreground">
        Quartz cron (with seconds) · IANA time zone such as America/Chicago · optional name. No
        triggers means the job only runs when triggered manually.
      </p>
    </div>
  )
}
