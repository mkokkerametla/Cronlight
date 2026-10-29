import { Info } from 'lucide-react'
import { Fragment, type ReactNode } from 'react'
import logo from '@/assets/logo.svg'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Skeleton } from '@/components/ui/skeleton'
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

const toneDot: Record<Tone, string> = {
  running: 'bg-emerald-500 dark:bg-emerald-400',
  idle: 'bg-muted-foreground',
  warning: 'bg-amber-500',
}

/** A cluster by its nodes, which may all be other processes; otherwise this process, which is the scheduler */
function schedulerState(scheduler: SchedulerInfo): { label: string; tone: Tone } {
  if (scheduler.clusterNodes) {
    // A node in standby keeps checking in; only this one's standby can be seen
    const alive = scheduler.clusterNodes.filter(
      n => n.alive && !(n.thisNode && scheduler.inStandbyMode)
    ).length
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
  known,
  onSchedulerChange,
  layout,
  onLayoutChange,
  nav,
}: {
  scheduler: SchedulerInfo | null
  /**
   * What is known before the scheduler answers: the name of the one asked for, and the schedulers the last one listed.
   * The title and the switch keep their place with them while it loads.
   */
  known?: { name?: string; schedulers: string[] }
  /** Shows another scheduler; offered when the app serves more than one */
  onSchedulerChange?: (name: string) => void
  /** The page's width and row density, offered in a menu when given */
  layout?: Layout
  onLayoutChange?: (change: Partial<Layout>) => void
  /** Switches between the dashboard's areas, beside the scheduler */
  nav?: ReactNode
}) {
  const state = scheduler && schedulerState(scheduler)
  const name = scheduler?.schedulerName ?? known?.name
  const schedulers = scheduler?.schedulers ?? known?.schedulers ?? []

  return (
    <header className="flex flex-wrap items-center gap-3">
      <div className="flex min-w-0 items-center gap-2.5">
        <img src={logo} alt="Cronlight" className="size-9 shrink-0" />
        <h1 className="truncate text-xl leading-tight font-semibold tracking-tight">
          {/* No stand-in name while the first answer is on its way: it would be wrong for every app */}
          {name ?? (
            <>
              <span className="sr-only">Loading the scheduler</span>
              <Skeleton aria-hidden className="h-6 w-36" />
            </>
          )}
        </h1>
        {/* The title names the scheduler, so the switch beside it shows only its arrow; screen readers still hear
            which scheduler it is on */}
        {name && schedulers.length > 1 && onSchedulerChange && (
          <Select value={name} onValueChange={onSchedulerChange}>
            <SelectTrigger size="sm" className="px-1.5" aria-label="Scheduler">
              {/* Radix leaves the value's own className out */}
              <span className="sr-only">
                <SelectValue />
              </span>
            </SelectTrigger>
            <SelectContent>
              {schedulers.map(name => (
                <SelectItem key={name} value={name}>
                  {name}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        )}
      </div>
      {nav}

      <div className="ml-auto flex items-center gap-2">
        {state && (
          // A status, so it keeps out of the blue that marks what can be clicked
          <Badge variant={state.tone === 'running' ? 'outline' : 'secondary'} className="gap-1.5">
            <span className={`size-1.5 rounded-full ${toneDot[state.tone]}`} />
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

type Rows = [string, string][]

/** "3 sec. ago", "2 min. ago", "5 hr. ago", from the server's count */
const ago = (seconds: number) =>
  seconds < 60
    ? `${seconds} sec. ago`
    : seconds < 3600
      ? `${Math.floor(seconds / 60)} min. ago`
      : `${Math.floor(seconds / 3600)} hr. ago`

function SystemInfo({ scheduler }: { scheduler: SchedulerInfo }) {
  const nodes = scheduler.clusterNodes
  const store = `${scheduler.jobStore}${scheduler.jobStoreClustered ? ' (clustered)' : ''}`
  const access = scheduler.canManage ? 'Manage' : 'Read-only'
  const sections: { label: string; rows: Rows }[] = nodes
    ? [
        {
          label: 'Scheduler',
          rows: [
            ['Version', scheduler.version],
            ['Job store', store],
            ['Your access', access],
          ],
        },
        {
          label: 'This process',
          rows: scheduler.started
            ? [
                ['Instance', scheduler.schedulerInstanceId],
                ['State', scheduler.inStandbyMode ? 'Standby' : 'Running'],
                ['Threads', String(scheduler.threadPoolSize)],
                ['Jobs executed', String(scheduler.numberOfJobsExecuted)],
                ['Running since', formatDateTime(scheduler.runningSince)],
              ]
            : [['State', 'Not started (manages only)']],
        },
      ]
    : [
        {
          label: 'Scheduler',
          rows: [
            ['Version', scheduler.version],
            ['Instance', scheduler.schedulerInstanceId],
            ['Job store', store],
            ['Threads', String(scheduler.threadPoolSize)],
            ['Jobs executed', String(scheduler.numberOfJobsExecuted)],
            ['Running since', formatDateTime(scheduler.runningSince)],
            ['Your access', access],
          ],
        },
      ]

  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button variant="outline" size="icon" aria-label="Scheduler details">
          <Info />
        </Button>
      </DropdownMenuTrigger>
      {/* Wider for node names; the plain menu keeps its width */}
      <DropdownMenuContent align="end" className={nodes ? 'w-80' : 'w-72'}>
        {sections.map((section, i) => (
          <Fragment key={section.label}>
            {i > 0 && <DropdownMenuSeparator />}
            <DropdownMenuLabel>{section.label}</DropdownMenuLabel>
            {!nodes && <DropdownMenuSeparator />}
            <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1.5 px-2 py-1.5 text-sm">
              {section.rows.map(([label, value]) => (
                <div key={label} className="contents">
                  <dt className="text-muted-foreground">{label}</dt>
                  <dd className="truncate text-right font-medium" title={value}>
                    {value}
                  </dd>
                </div>
              ))}
            </dl>
          </Fragment>
        ))}
        {nodes && (
          <>
            <DropdownMenuSeparator />
            <DropdownMenuLabel>Nodes</DropdownMenuLabel>
            {nodes.length === 0 ? (
              <p className="px-2 py-1.5 text-sm text-muted-foreground">None checked in</p>
            ) : (
              <ul className="space-y-1.5 px-2 py-1.5 text-sm">
                {nodes.map(n => (
                  <li
                    key={n.instanceId}
                    className="flex items-center justify-between gap-2"
                    title={formatDateTime(n.lastCheckinUtc)}
                  >
                    <span className="min-w-0 truncate font-mono text-xs" title={n.instanceId}>
                      {n.instanceId}
                    </span>
                    <span className="flex shrink-0 items-center gap-1.5 text-xs text-muted-foreground">
                      {n.thisNode && <Badge variant="outline">this node</Badge>}
                      {n.alive ? (
                        `checked in ${ago(n.secondsSinceCheckin)}`
                      ) : (
                        <span className="text-amber-700 dark:text-amber-400">
                          {`not responding · ${ago(n.secondsSinceCheckin)}`}
                        </span>
                      )}
                    </span>
                  </li>
                ))}
              </ul>
            )}
          </>
        )}
      </DropdownMenuContent>
    </DropdownMenu>
  )
}
