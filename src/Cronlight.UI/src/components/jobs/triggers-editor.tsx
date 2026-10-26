import { ExternalLink, Plus, X } from 'lucide-react'
import { useCallback, useEffect, useId, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { CronPreview } from '@/components/jobs/cron-preview'
import { formatDateTime, formatSeconds } from '@/lib/format'
import {
  emptyTrigger,
  fromLocalInput,
  unitSeconds,
  type IntervalUnit,
  type TriggerDraft,
} from '@/lib/job-form'
import type { EditableTriggerKind } from '@/types'

interface Props {
  triggers: TriggerDraft[]
  onChange: (triggers: TriggerDraft[]) => void
  /** Told whether a cron expression is shown as invalid, so the form can refuse to save */
  onInvalidChange?: (invalid: boolean) => void
}

// Quartz.NET's own reference, for the version Cronlight runs on
const cronSyntax =
  'https://www.quartz-scheduler.net/documentation/quartz-3.x/tutorial/crontriggers.html'

const kindLabels: Record<EditableTriggerKind, string> = {
  Cron: 'Cron',
  Once: 'One time',
  Interval: 'Interval',
}

const skipLabels: Record<EditableTriggerKind, string> = {
  Cron: 'Skip missed runs (when the scheduler was down or busy) instead of running once when it can',
  Once: 'Skip the run if the scheduler misses its time (it then never runs) instead of running when it can',
  Interval:
    'Skip missed runs (when the scheduler was down or busy) instead of running once when it can',
}

export function TriggersEditor({ triggers, onChange, onInvalidChange }: Props) {
  const id = useId()
  const update = (index: number, patch: Partial<TriggerDraft>) =>
    onChange(triggers.map((t, i) => (i === index ? { ...t, ...patch } : t)))

  // Triggers whose cron expression is shown as invalid, by id
  const [invalid, setInvalid] = useState<ReadonlySet<number>>(() => new Set())
  const markInvalid = useCallback(
    (triggerId: number, value: boolean) =>
      setInvalid(current => {
        if (current.has(triggerId) === value) return current
        const next = new Set(current)
        if (value) next.add(triggerId)
        else next.delete(triggerId)
        return next
      }),
    []
  )
  const anyInvalid = triggers.some(t => t.kind === 'Cron' && invalid.has(t.id))
  useEffect(() => {
    onInvalidChange?.(anyInvalid)
  }, [anyInvalid, onInvalidChange])

  return (
    <div className="space-y-3">
      {triggers.map((trigger, index) => (
        <div key={trigger.id} className="space-y-1.5 rounded-md border p-2.5">
          <div className="flex flex-wrap gap-2">
            <Select
              value={trigger.kind}
              onValueChange={kind => update(index, { kind: kind as EditableTriggerKind })}
            >
              <SelectTrigger className="w-32" aria-label="Trigger kind">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {Object.entries(kindLabels).map(([kind, label]) => (
                  <SelectItem key={kind} value={kind}>
                    {label}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
            {trigger.kind === 'Cron' && (
              <>
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
              </>
            )}
            {trigger.kind === 'Once' && (
              <Input
                type="datetime-local"
                className="min-w-0 flex-[2_1_14rem]"
                value={trigger.startAt}
                onChange={e => update(index, { startAt: e.target.value })}
                aria-label="Run at"
              />
            )}
            {trigger.kind === 'Interval' && (
              <>
                <Input
                  type="number"
                  min={1}
                  className="min-w-0 flex-[1_1_5rem]"
                  value={trigger.every}
                  onChange={e => update(index, { every: e.target.value })}
                  placeholder="Every"
                  aria-label="Interval"
                />
                <Select
                  value={trigger.unit}
                  onValueChange={unit => update(index, { unit: unit as IntervalUnit })}
                >
                  <SelectTrigger className="w-28" aria-label="Interval unit">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    {Object.keys(unitSeconds).map(unit => (
                      <SelectItem key={unit} value={unit}>
                        {unit}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              </>
            )}
            <Input
              className="min-w-0 flex-[1_1_9rem]"
              value={trigger.name}
              onChange={e => update(index, { name: e.target.value })}
              placeholder="Name (optional)"
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
          {trigger.kind === 'Interval' && (
            <div className="flex flex-wrap gap-2">
              <Input
                type="datetime-local"
                className="min-w-0 flex-[2_1_14rem]"
                value={trigger.startAt}
                onChange={e => update(index, { startAt: e.target.value })}
                aria-label="First run"
                title="First run, in your local time; empty starts now"
              />
              <Input
                type="number"
                min={2}
                className="min-w-0 flex-[1_1_9rem]"
                value={trigger.runs}
                onChange={e => update(index, { runs: e.target.value })}
                placeholder="Runs (empty: forever)"
                aria-label="Number of runs"
              />
            </div>
          )}
          {trigger.kind === 'Cron' && (
            <CronPreview
              expression={trigger.cronExpression}
              timeZone={trigger.timeZone}
              onInvalidChange={value => markInvalid(trigger.id, value)}
            />
          )}
          {trigger.kind !== 'Cron' && <SchedulePreview trigger={trigger} />}
          <div className="flex items-center gap-2">
            <Checkbox
              id={`${id}-skip-${trigger.id}`}
              checked={trigger.skipMissed}
              onCheckedChange={checked => update(index, { skipMissed: checked === true })}
            />
            <Label htmlFor={`${id}-skip-${trigger.id}`} className="text-xs font-normal">
              {skipLabels[trigger.kind]}
            </Label>
          </div>
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
        Cron: Quartz cron, with seconds (
        <a
          href={cronSyntax}
          target="_blank"
          rel="noopener noreferrer"
          className="inline-flex items-center gap-0.5 underline hover:text-foreground"
        >
          Quartz cron syntax <ExternalLink className="size-3" />
        </a>
        ), and an IANA time zone such as America/Chicago. One time and interval: times in your local
        time. Names are optional. No triggers means the job only runs when triggered manually.
      </p>
    </div>
  )
}

/** When a one-time or interval trigger runs, as the server will read it */
function SchedulePreview({ trigger }: { trigger: TriggerDraft }) {
  const start = fromLocalInput(trigger.startAt)
  if (trigger.kind === 'Once')
    return (
      <p className="text-xs text-muted-foreground">
        {start ? `Runs once, ${formatDateTime(start)}` : 'Choose when it runs.'}
      </p>
    )

  const every = Number(trigger.every)
  if (!Number.isInteger(every) || every < 1)
    return <p className="text-xs text-muted-foreground">Enter how often it runs.</p>
  const runs = Number(trigger.runs)
  if (runs === 1)
    return (
      <p className="text-xs text-destructive">
        An interval trigger runs at least twice. For a single run, choose One time.
      </p>
    )
  return (
    <p className="text-xs text-muted-foreground">
      Every {formatSeconds(every * unitSeconds[trigger.unit])}, starting{' '}
      {start ? formatDateTime(start) : 'now'},{' '}
      {trigger.runs.trim() && Number.isInteger(runs) && runs > 0
        ? `${runs} runs in all`
        : 'forever'}
    </p>
  )
}
