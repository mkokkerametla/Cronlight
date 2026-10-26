import type { LucideIcon } from 'lucide-react'
import { Activity, CircleX, Layers, Lock, OctagonAlert, PauseCircle } from 'lucide-react'
import { Card, CardContent } from '@/components/ui/card'
import { Skeleton } from '@/components/ui/skeleton'
import { cn } from '@/lib/utils'

export type StatKey = 'all' | 'running' | 'Paused' | 'Unloadable' | 'Error' | 'failed'

interface StatsProps {
  total: number
  running: number
  paused: number
  unloadable: number
  /** Jobs Quartz stopped, which do not run until resumed; their card shows only when there are some. */
  error: number
  /** Failed executions in the last 24 hours; null when history is not enabled. */
  failed: number | null
  active: StatKey | null
  onSelect: (key: StatKey) => void
}

const rowClass = (cards: number) =>
  cn(
    'grid grid-cols-2 gap-3',
    { 4: 'lg:grid-cols-4', 5: 'lg:grid-cols-5', 6: 'lg:grid-cols-6' }[cards]
  )

/**
 * The stat row while the dashboard loads. Whether history is on is not known yet, so it holds the five cards a
 * dashboard with history shows, the usual setup, and the row does not move when they arrive.
 */
export function StatsSkeleton() {
  return (
    <div className={rowClass(5)}>
      {Array.from({ length: 5 }, (_, i) => (
        <Skeleton key={i} className="h-[74px] rounded-xl" />
      ))}
    </div>
  )
}

export function Stats({
  total,
  running,
  paused,
  unloadable,
  error,
  failed,
  active,
  onSelect,
}: StatsProps) {
  const cards: { key: StatKey; label: string; value: number; icon: LucideIcon; tone?: string }[] = [
    { key: 'all', label: 'Total jobs', value: total, icon: Layers },
    {
      key: 'running',
      label: 'Running now',
      value: running,
      icon: Activity,
      tone: running ? 'text-emerald-600 dark:text-emerald-400' : undefined,
    },
    {
      key: 'Paused',
      label: 'Paused',
      value: paused,
      icon: PauseCircle,
      tone: paused ? 'text-amber-600 dark:text-amber-400' : undefined,
    },
    ...(failed === null
      ? []
      : [
          {
            key: 'failed' as const,
            label: 'Failed (24h)',
            value: failed,
            icon: CircleX,
            tone: failed ? 'text-destructive' : undefined,
          },
        ]),
    {
      key: 'Unloadable',
      label: 'Unloadable',
      value: unloadable,
      icon: Lock,
      tone: unloadable ? 'text-destructive' : undefined,
    },
    ...(error > 0
      ? [
          {
            key: 'Error' as const,
            label: 'Error',
            value: error,
            icon: OctagonAlert,
            tone: 'text-destructive',
          },
        ]
      : []),
  ]

  return (
    <div className={rowClass(cards.length)}>
      {cards.map(({ key, label, value, icon: Icon, tone }) => (
        <button
          key={key}
          type="button"
          onClick={() => onSelect(key)}
          className="rounded-xl text-left focus-visible:ring-3 focus-visible:ring-ring/50 focus-visible:outline-none"
          aria-pressed={active === key}
        >
          <Card
            className={cn(
              'gap-0 py-0 transition-colors hover:bg-muted/50',
              active === key && 'bg-primary/5 ring-2 ring-primary'
            )}
          >
            <CardContent className="flex items-center justify-between p-4">
              <div>
                <div className={cn('text-2xl font-semibold tabular-nums', tone)}>{value}</div>
                <div className="text-xs text-muted-foreground">{label}</div>
              </div>
              <Icon className={cn('size-5 text-muted-foreground', tone)} />
            </CardContent>
          </Card>
        </button>
      ))}
    </div>
  )
}
