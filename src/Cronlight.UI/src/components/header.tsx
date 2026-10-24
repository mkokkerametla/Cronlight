import { CalendarClock, Info } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { ThemeToggle } from '@/components/theme-toggle'
import { formatDateTime } from '@/lib/format'
import type { SchedulerInfo } from '@/types'

export function Header({ scheduler }: { scheduler: SchedulerInfo | null }) {
  const state = !scheduler
    ? null
    : !scheduler.started
      ? 'Stopped'
      : scheduler.inStandbyMode
        ? 'Standby'
        : 'Running'

  return (
    <header className="flex flex-wrap items-center gap-3">
      <div className="flex min-w-0 items-center gap-2.5">
        <div className="flex size-9 shrink-0 items-center justify-center rounded-lg bg-primary text-primary-foreground">
          <CalendarClock className="size-5" />
        </div>
        <div className="min-w-0">
          <h1 className="truncate text-lg leading-tight font-semibold">
            {scheduler?.schedulerName ?? 'Quartz'}
          </h1>
          <p className="text-xs text-muted-foreground">Cronlight · Quartz.NET</p>
        </div>
      </div>

      <div className="ml-auto flex items-center gap-2">
        {state && (
          <Badge variant={state === 'Running' ? 'default' : 'secondary'} className="gap-1.5">
            <span
              className={`size-1.5 rounded-full ${state === 'Running' ? 'bg-emerald-400' : 'bg-muted-foreground'}`}
            />
            {state}
          </Badge>
        )}
        {scheduler && !scheduler.canManage && <Badge variant="outline">Read-only</Badge>}
        {scheduler && <SystemInfo scheduler={scheduler} />}
        <ThemeToggle />
      </div>
    </header>
  )
}

function SystemInfo({ scheduler }: { scheduler: SchedulerInfo }) {
  const rows: [string, string][] = [
    ['Version', scheduler.version],
    ['Instance', scheduler.schedulerInstanceId],
    ['Job store', `${scheduler.jobStore}${scheduler.jobStoreClustered ? ' (clustered)' : ''}`],
    ['Threads', String(scheduler.threadPoolSize)],
    ['Jobs executed', String(scheduler.numberOfJobsExecuted)],
    ['Running since', formatDateTime(scheduler.runningSince)],
    ['Your access', scheduler.canManage ? 'Manage' : 'Read-only'],
  ]

  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button variant="outline" size="icon" aria-label="Scheduler details">
          <Info />
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="w-72">
        <DropdownMenuLabel>Scheduler</DropdownMenuLabel>
        <DropdownMenuSeparator />
        <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1.5 px-2 py-1.5 text-sm">
          {rows.map(([label, value]) => (
            <div key={label} className="contents">
              <dt className="text-muted-foreground">{label}</dt>
              <dd className="truncate text-right font-medium" title={value}>
                {value}
              </dd>
            </div>
          ))}
        </dl>
      </DropdownMenuContent>
    </DropdownMenu>
  )
}
