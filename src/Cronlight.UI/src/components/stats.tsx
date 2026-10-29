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
  /** Jobs whose type this app cannot load; their card shows only when there are some. */
  unloadable: number
  /** Jobs Quartz stopped, which do not run until resumed; their card shows only when there are some. */
  error: number
  /** Failed executions in the last 24 hours; null when history is not enabled. */
  failed: number | null
  active: StatKey | null
  onSelect: (key: StatKey) => void
}

// On a phone the cards are a compact row of four, numbers and labels only, so the list starts near the top
const rowClass = (cards: number) =>
  cn(
    'grid grid-cols-4 gap-2 sm:grid-cols-2 sm:gap-3',
    { 4: 'lg:grid-cols-4', 5: 'lg:grid-cols-5', 6: 'lg:grid-cols-6' }[cards]
  )

/**
 * The stat row while the dashboard loads. Whether history is on is not known yet, so it holds the four cards a
 * dashboard with history shows when every job loads, the usual setup, and the row does not move when they arrive.
 */
export function StatsSkeleton() {
  return (
    <div className={rowClass(4)}>
      {Array.from({ length: 4 }, (_, i) => (
        <Skeleton key={i} className="h-16 rounded-xl sm:h-20" />
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
  const cards: {
    key: StatKey
    label: string
    /** The end of the label, which a phone's narrow card leaves out */
    suffix?: string
    value: number
    icon: LucideIcon
    tone?: string
  }[] = [
    { key: 'all', label: 'Total jobs', value: total, icon: Layers },
    {
      key: 'running',
      label: 'Running',
      suffix: 'now',
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
    // Only apps sharing their scheduler with another app have jobs they cannot load
    ...(unloadable > 0
      ? [
          {
            key: 'Unloadable' as const,
            label: 'Unloadable',
            value: unloadable,
            icon: Lock,
            tone: 'text-destructive',
          },
        ]
      : []),
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
      {cards.map(({ key, label, suffix, value, icon: Icon, tone }) => (
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
              // What is on screen is underlined, as a tab is, rather than ringed like an alert
              active === key && 'shadow-[inset_0_-2px_0_var(--primary)]'
            )}
          >
            <CardContent className="flex items-center justify-between px-2 py-2.5 sm:p-4">
              <div className="min-w-0">
                {/* A count of 0 steps back, so the one that needs attention stands out */}
                <div
                  className={cn(
                    'text-lg font-semibold tabular-nums sm:text-2xl',
                    tone ?? (value === 0 && 'text-muted-foreground')
                  )}
                >
                  {value.toLocaleString()}
                </div>
                <div
                  className={cn(
                    'truncate text-[11px] sm:text-xs',
                    active === key ? 'text-foreground' : 'text-muted-foreground'
                  )}
                  title={suffix ? `${label} ${suffix}` : label}
                >
                  {label}
                  {suffix && (
                    <>
                      {' '}
                      <span className="max-sm:hidden">{suffix}</span>
                    </>
                  )}
                </div>
              </div>
              <Icon className={cn('hidden size-5 shrink-0 text-muted-foreground sm:block', tone)} />
            </CardContent>
          </Card>
        </button>
      ))}
    </div>
  )
}
