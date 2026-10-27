import { Info } from 'lucide-react'
import logo from '@/assets/logo.svg'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { LayoutMenu } from '@/components/layout-menu'
import { ThemeToggle } from '@/components/theme-toggle'
import type { Layout } from '@/hooks/use-layout'
import { formatDateTime } from '@/lib/format'
import type { SchedulerInfo } from '@/types'

type Tone = 'running' | 'idle' | 'warning'

const dot: Record<Tone, string> = {
  running: 'bg-emerald-400',
  idle: 'bg-muted-foreground',
  warning: 'bg-amber-500',
}

/** A cluster by its nodes, which may all be other processes; otherwise this process, which is the scheduler */
function schedulerState(scheduler: SchedulerInfo): { label: string; tone: Tone } {
  if (scheduler.clusterNodes) {
    const alive = scheduler.clusterNodes.filter(n => n.alive).length
    return alive > 0
      ? { label: `Running · ${alive} ${alive === 1 ? 'node' : 'nodes'}`, tone: 'running' }
      : { label: 'No nodes running', tone: 'warning' }
  }
  if (!scheduler.started) return { label: 'Stopped', tone: 'idle' }
  return scheduler.inStandbyMode
    ? { label: 'Standby', tone: 'idle' }
    : { label: 'Running', tone: 'running' }
}

export function Header({
  scheduler,
  onSchedulerChange,
  layout,
  onLayoutChange,
}: {
  scheduler: SchedulerInfo | null
  /** Shows another scheduler; offered when the app serves more than one */
  onSchedulerChange?: (name: string) => void
  /** The page's width and row density, offered in a menu when given */
  layout?: Layout
  onLayoutChange?: (change: Partial<Layout>) => void
}) {
  const state = scheduler && schedulerState(scheduler)

  return (
    <header className="flex flex-wrap items-center gap-3">
      <div className="flex min-w-0 items-center gap-2.5">
        <img src={logo} alt="Cronlight" className="size-9 shrink-0" />
        <div className="min-w-0">
          <h1 className="truncate text-lg leading-tight font-semibold">
            {scheduler?.schedulerName ?? 'Quartz'}
          </h1>
          <p className="text-xs text-muted-foreground">Cronlight · Quartz.NET</p>
        </div>
      </div>
      {scheduler && scheduler.schedulers.length > 1 && onSchedulerChange && (
        <Select value={scheduler.schedulerName} onValueChange={onSchedulerChange}>
          <SelectTrigger size="sm" className="max-w-56" aria-label="Scheduler">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            {scheduler.schedulers.map(name => (
              <SelectItem key={name} value={name}>
                {name}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
      )}

      <div className="ml-auto flex items-center gap-2">
        {state && (
          <Badge variant={state.tone === 'running' ? 'default' : 'secondary'} className="gap-1.5">
            <span className={`size-1.5 rounded-full ${dot[state.tone]}`} />
            {state.label}
          </Badge>
        )}
        {scheduler && !scheduler.canManage && <Badge variant="outline">Read-only</Badge>}
        {scheduler && <SystemInfo scheduler={scheduler} />}
        {layout && onLayoutChange && <LayoutMenu layout={layout} onChange={onLayoutChange} />}
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
