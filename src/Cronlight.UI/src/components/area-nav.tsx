import { LayoutDashboard, Sparkles, type LucideIcon } from 'lucide-react'
import { cn } from '@/lib/utils'
import type { Area } from '@/lib/view-state'

const labels: Record<Area, { label: string; icon: LucideIcon }> = {
  dashboard: { label: 'Dashboard', icon: LayoutDashboard },
  assistant: { label: 'AI mode', icon: Sparkles },
}

/**
 * Switches between the dashboard's areas, as a segmented control in the header, so the job list keeps its full width.
 * Shown only when there is more than one area to switch between.
 */
export function AreaNav({
  areas,
  area,
  onAreaChange,
}: {
  areas: Area[]
  area: Area
  onAreaChange: (area: Area) => void
}) {
  return (
    <nav aria-label="Areas" className="flex items-center gap-0.5 rounded-lg bg-muted p-0.5">
      {areas.map(a => {
        const { label, icon: Icon } = labels[a]
        const current = a === area
        return (
          <button
            key={a}
            type="button"
            aria-current={current ? 'page' : undefined}
            onClick={() => onAreaChange(a)}
            className={cn(
              'flex items-center gap-1.5 rounded-md px-2.5 py-1 text-sm font-medium transition-colors',
              current
                ? 'bg-background text-foreground shadow-sm dark:bg-input/60'
                : 'text-foreground/70 hover:text-foreground'
            )}
          >
            <Icon className={cn('size-4', current && a === 'assistant' && 'text-link')} />
            {label}
          </button>
        )
      })}
    </nav>
  )
}
