import { useEffect, useId, useState, type KeyboardEvent, type ReactNode } from 'react'
import { Dialog } from 'radix-ui'
import { Pause, Play, PlayCircle, Search, SquareArrowOutUpRight } from 'lucide-react'
import { api } from '@/api'
import { Kbd } from '@/components/ui/kbd'
import { canResume, jobKey } from '@/lib/format'
import { cn } from '@/lib/utils'
import type { Job, JobAction, JobPage } from '@/types'

/** Something the palette can do, such as switching tab or opening New job */
export interface PaletteCommand {
  label: string
  icon: ReactNode
  run: () => void
}

interface Item extends PaletteCommand {
  id: string
  hint: string
}

const JOBS_SHOWN = 5

/**
 * Ctrl+K (⌘K on a Mac): find a job and open, run, pause or resume it, or run one of the dashboard's commands, from the
 * keyboard. Jobs are searched on the server, as the job list's search box does.
 */
export function CommandPalette({
  open,
  onOpenChange,
  commands,
  canManage,
  onOpenJob,
  onRun,
}: {
  open: boolean
  onOpenChange: (open: boolean) => void
  commands: PaletteCommand[]
  canManage: boolean
  onOpenJob: (job: Job) => void
  onRun: (job: Job, action: JobAction) => void
}) {
  const [query, setQuery] = useState('')
  const [found, setFound] = useState<{ search: string; jobs: Job[] } | null>(null)
  const [active, setActive] = useState(0)
  const id = useId()

  // Every close goes through here, so each opening starts with an empty search
  const change = (next: boolean) => {
    if (!next) {
      setQuery('')
      setActive(0)
    }
    onOpenChange(next)
  }

  useEffect(() => {
    const onKeyDown = (e: globalThis.KeyboardEvent) => {
      if (e.key.toLowerCase() !== 'k' || !(e.ctrlKey || e.metaKey) || e.altKey || e.shiftKey) return
      // Not over a panel or dialog that has the keyboard
      if (!open && document.querySelector('[role="dialog"], [role="alertdialog"]')) return
      e.preventDefault()
      change(!open)
    }
    document.addEventListener('keydown', onKeyDown)
    return () => document.removeEventListener('keydown', onKeyDown)
  })

  const search = query.trim()
  useEffect(() => {
    if (!open || !search) return
    const controller = new AbortController()
    const timeout = setTimeout(() => {
      const params = new URLSearchParams({ page: '1', pageSize: String(JOBS_SHOWN), search })
      api<JobPage>(`jobs/search?${params}`, { signal: controller.signal })
        .then(page => setFound({ search, jobs: page.jobs }))
        .catch((err: Error) => {
          if (err.name !== 'AbortError') setFound({ search, jobs: [] })
        })
    }, 150)
    return () => {
      clearTimeout(timeout)
      controller.abort()
    }
  }, [open, search])

  const jobs = search && found?.search === search ? found.jobs : []
  const items: Item[] = [
    ...jobs.flatMap(job => {
      const key = jobKey(job)
      const actions: Item[] = [
        {
          id: `open:${key}`,
          label: job.name,
          hint: job.group === 'DEFAULT' ? 'Open job' : `Open job · ${job.group}`,
          icon: <SquareArrowOutUpRight />,
          run: () => onOpenJob(job),
        },
      ]
      if (canManage && job.loadable) {
        actions.push({
          id: `trigger:${key}`,
          label: `Run ${job.name} now`,
          hint: 'Run',
          icon: <Play />,
          run: () => onRun(job, 'trigger'),
        })
        const resume = canResume(job)
        if (resume || job.triggers.length > 0)
          actions.push({
            id: `${resume ? 'resume' : 'pause'}:${key}`,
            label: `${resume ? 'Resume' : 'Pause'} ${job.name}`,
            hint: resume ? 'Resume' : 'Pause',
            icon: resume ? <PlayCircle /> : <Pause />,
            run: () => onRun(job, resume ? 'resume' : 'pause'),
          })
      }
      return actions
    }),
    ...commands
      .filter(c => c.label.toLowerCase().includes(search.toLowerCase()))
      .map(c => ({ ...c, id: `command:${c.label}`, hint: 'Command' })),
  ]
  const current = Math.min(active, items.length - 1)

  // Arrow keys can move past the list's height
  useEffect(() => {
    document.getElementById(`${id}-${current}`)?.scrollIntoView?.({ block: 'nearest' })
  }, [id, current])

  const choose = (item: Item) => {
    change(false)
    item.run()
  }
  const onKeyDown = (e: KeyboardEvent) => {
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault()
      const step = e.key === 'ArrowDown' ? 1 : -1
      setActive((current + step + items.length) % Math.max(items.length, 1))
    } else if (e.key === 'Enter' && items[current]) {
      e.preventDefault()
      choose(items[current])
    }
  }

  return (
    <Dialog.Root open={open} onOpenChange={change}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 z-50 bg-black/10 supports-backdrop-filter:backdrop-blur-xs" />
        <Dialog.Content
          className="fixed top-[15vh] left-1/2 z-50 w-[calc(100%-2rem)] max-w-lg -translate-x-1/2 overflow-hidden rounded-xl bg-popover text-popover-foreground ring-1 ring-foreground/10 outline-none"
          aria-describedby={undefined}
        >
          <Dialog.Title className="sr-only">Find a job or command</Dialog.Title>
          <div className="flex items-center gap-2 border-b px-3">
            <Search className="size-4 shrink-0 text-muted-foreground" aria-hidden />
            <input
              role="combobox"
              aria-expanded
              aria-controls={`${id}-list`}
              aria-activedescendant={items[current] ? `${id}-${current}` : undefined}
              aria-label="Find a job or command"
              placeholder="Find a job or command…"
              className="h-11 w-full bg-transparent text-sm outline-none placeholder:text-muted-foreground"
              value={query}
              onChange={e => {
                setQuery(e.target.value)
                setActive(0)
              }}
              onKeyDown={onKeyDown}
            />
            <Kbd aria-hidden>Esc</Kbd>
          </div>
          <ul
            id={`${id}-list`}
            role="listbox"
            aria-label="Results"
            className="max-h-80 overflow-y-auto p-1"
          >
            {items.map((item, i) => (
              <li
                key={item.id}
                id={`${id}-${i}`}
                role="option"
                aria-selected={i === current}
                className={cn(
                  'flex cursor-pointer items-center gap-2 rounded-md px-2 py-1.5 text-sm [&_svg]:size-4 [&_svg]:shrink-0 [&_svg]:text-muted-foreground',
                  i === current && 'bg-accent text-accent-foreground'
                )}
                onPointerMove={() => i !== current && setActive(i)}
                onClick={() => choose(item)}
              >
                {item.icon}
                <span className="min-w-0 flex-1 truncate">{item.label}</span>
                <span className="text-xs text-muted-foreground">{item.hint}</span>
              </li>
            ))}
          </ul>
          {items.length === 0 && (
            <p className="px-3 pb-4 text-sm text-muted-foreground">
              {search && found?.search !== search ? 'Searching…' : 'Nothing matches.'}
            </p>
          )}
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  )
}
