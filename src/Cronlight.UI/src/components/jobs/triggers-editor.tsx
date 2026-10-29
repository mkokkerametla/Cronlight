import { ExternalLink, Plus, X } from 'lucide-react'
import { useCallback, useEffect, useId, useState, type ReactNode } from 'react'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
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
import { cn } from '@/lib/utils'
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

/** Schedules people often want, so a cron expression can start from one rather than from the syntax */
const commonSchedules = [
  { label: 'Every minute', expression: '0 * * ? * *' },
  { label: 'Every 5 minutes', expression: '0 0/5 * * * ?' },
  { label: 'Every hour', expression: '0 0 * ? * *' },
  { label: 'Every day at midnight', expression: '0 0 0 * * ?' },
  { label: 'Every day at 02:00', expression: '0 0 2 * * ?' },
  { label: 'Weekdays at 09:00', expression: '0 0 9 ? * MON-FRI' },
  { label: 'Mondays at 09:00', expression: '0 0 9 ? * MON' },
  { label: 'First of the month at midnight', expression: '0 0 0 1 * ?' },
]

/** The time zones suggested as one is typed: the browser's own first, then every zone it knows */
const timeZones = [
  ...new Set([
    Intl.DateTimeFormat().resolvedOptions().timeZone,
    ...(typeof Intl.supportedValuesOf === 'function' ? Intl.supportedValuesOf('timeZone') : []),
  ]),
]

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
      {triggers.map((trigger, index) => {
        const field = (name: string) => `${id}-${name}-${trigger.id}`
        return (
          <div key={trigger.id} className="space-y-1.5 rounded-md border p-2.5">
            <div className="flex flex-wrap items-end gap-2">
              <Field id={field('kind')} label="Trigger kind" className="w-32">
                <Select
                  value={trigger.kind}
                  onValueChange={kind => update(index, { kind: kind as EditableTriggerKind })}
                >
                  <SelectTrigger id={field('kind')} className="w-full">
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
              </Field>
              {trigger.kind === 'Cron' && (
                <>
                  <Field
                    id={field('cron')}
                    label="Cron expression"
                    className="flex-[2_1_14rem]"
                    action={
                      <CommonSchedules
                        onPick={expression => update(index, { cronExpression: expression })}
                      />
                    }
                  >
                    <Input
                      id={field('cron')}
                      className="font-mono"
                      value={trigger.cronExpression}
                      onChange={e => update(index, { cronExpression: e.target.value })}
                      placeholder="e.g. 0 0/5 * * * ?"
                      aria-invalid={invalid.has(trigger.id) || undefined}
                      aria-describedby={field('cron-check')}
                    />
                  </Field>
                  <Field id={field('zone')} label="Time zone" className="flex-[1_1_9rem]">
                    <Input
                      id={field('zone')}
                      list={`${id}-zones`}
                      value={trigger.timeZone}
                      onChange={e => update(index, { timeZone: e.target.value })}
                      placeholder="Empty: UTC"
                      title="An IANA time zone, such as America/Chicago"
                    />
                  </Field>
                </>
              )}
              {trigger.kind === 'Once' && (
                <Field id={field('run-at')} label="Run at" className="flex-[2_1_14rem]">
                  <Input
                    id={field('run-at')}
                    type="datetime-local"
                    value={trigger.startAt}
                    onChange={e => update(index, { startAt: e.target.value })}
                  />
                </Field>
              )}
              {trigger.kind === 'Interval' && (
                <>
                  <Field id={field('every')} label="Interval" className="flex-[1_1_5rem]">
                    <Input
                      id={field('every')}
                      type="number"
                      min={1}
                      value={trigger.every}
                      onChange={e => update(index, { every: e.target.value })}
                      placeholder="e.g. 15"
                    />
                  </Field>
                  <Field id={field('unit')} label="Interval unit" className="w-28">
                    <Select
                      value={trigger.unit}
                      onValueChange={unit => update(index, { unit: unit as IntervalUnit })}
                    >
                      <SelectTrigger id={field('unit')} className="w-full">
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
                  </Field>
                </>
              )}
              <Field id={field('name')} label="Trigger name" className="flex-[1_1_9rem]">
                <Input
                  id={field('name')}
                  value={trigger.name}
                  onChange={e => update(index, { name: e.target.value })}
                  placeholder="Optional"
                />
              </Field>
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
                <Field id={field('start')} label="First run" className="flex-[2_1_14rem]">
                  <Input
                    id={field('start')}
                    type="datetime-local"
                    value={trigger.startAt}
                    onChange={e => update(index, { startAt: e.target.value })}
                    title="In your local time; empty starts now"
                  />
                </Field>
                <Field id={field('runs')} label="Number of runs" className="flex-[1_1_9rem]">
                  <Input
                    id={field('runs')}
                    type="number"
                    min={2}
                    value={trigger.runs}
                    onChange={e => update(index, { runs: e.target.value })}
                    placeholder="Empty: forever"
                  />
                </Field>
              </div>
            )}
            {trigger.kind === 'Cron' && (
              <CronPreview
                messageId={field('cron-check')}
                expression={trigger.cronExpression}
                timeZone={trigger.timeZone}
                onInvalidChange={value => markInvalid(trigger.id, value)}
              />
            )}
            {/* Where someone is stuck on cron: the one link to the syntax */}
            {trigger.kind === 'Cron' &&
              (!trigger.cronExpression.trim() || invalid.has(trigger.id)) && (
                <p className="text-xs text-muted-foreground">
                  Not sure? See <CronSyntaxLink />
                </p>
              )}
            {trigger.kind !== 'Cron' && <SchedulePreview trigger={trigger} />}
            <div className="flex items-center gap-2">
              <Checkbox
                id={field('skip')}
                checked={trigger.skipMissed}
                onCheckedChange={checked => update(index, { skipMissed: checked === true })}
              />
              <Label htmlFor={field('skip')} className="text-xs font-normal">
                {skipLabels[trigger.kind]}
              </Label>
            </div>
            {trigger.calendarName && (
              <p className="text-xs text-muted-foreground">
                Skips the times excluded by its calendar,{' '}
                <span className="font-medium text-foreground">{trigger.calendarName}</span>, kept
                while the trigger keeps its name
              </p>
            )}
          </div>
        )
      })}
      <Button
        type="button"
        variant="outline"
        size="sm"
        onClick={() => onChange([...triggers, emptyTrigger()])}
      >
        <Plus /> Add trigger
      </Button>
      <ul className="max-w-prose space-y-0.5 text-xs text-muted-foreground">
        <li>One time and interval: in your local time.</li>
        <li>No triggers: the job runs only when triggered manually.</li>
      </ul>
      {/* One list for every trigger's time zone field */}
      <datalist id={`${id}-zones`}>
        {timeZones.map(zone => (
          <option key={zone} value={zone} />
        ))}
      </datalist>
    </div>
  )
}

/** A trigger's field with its label above it, so a field that holds a value still says what it is */
function Field({
  id,
  label,
  className,
  action,
  children,
}: {
  id: string
  label: string
  className?: string
  /** A control beside the label, such as a menu that fills the field */
  action?: ReactNode
  children: ReactNode
}) {
  return (
    <div className={cn('flex min-w-0 flex-col gap-1', className)}>
      <div className="flex items-baseline justify-between gap-2">
        <Label htmlFor={id} className="text-xs font-normal text-muted-foreground">
          {label}
        </Label>
        {action}
      </div>
      {children}
    </div>
  )
}

/** Fills a cron field with a schedule people often want */
function CommonSchedules({ onPick }: { onPick: (expression: string) => void }) {
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button type="button" variant="link" size="sm" className="h-auto p-0 text-xs">
          Common schedules
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end">
        {commonSchedules.map(s => (
          <DropdownMenuItem
            key={s.expression}
            onSelect={() => onPick(s.expression)}
            className="justify-between gap-6"
          >
            {s.label}
            <span className="font-mono text-xs text-muted-foreground">{s.expression}</span>
          </DropdownMenuItem>
        ))}
      </DropdownMenuContent>
    </DropdownMenu>
  )
}

function CronSyntaxLink() {
  return (
    <a
      href={cronSyntax}
      target="_blank"
      rel="noopener noreferrer"
      className="inline-flex items-center gap-0.5 underline hover:text-foreground"
    >
      Quartz cron syntax <ExternalLink className="size-3" />
    </a>
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
