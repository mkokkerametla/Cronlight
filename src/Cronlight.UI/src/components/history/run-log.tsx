import { useEffect, useState } from 'react'
import { Loader2 } from 'lucide-react'
import { api } from '@/api'
import { cn } from '@/lib/utils'
import type { RunLogEntry } from '@/types'

const levelTone: Partial<Record<RunLogEntry['level'], string>> = {
  Warning: 'text-amber-700 dark:text-amber-400',
  Error: 'text-destructive',
  Critical: 'text-destructive',
}

const time = new Intl.DateTimeFormat(undefined, { timeStyle: 'medium' })

/** What a run logged while it ran, loaded when shown: logs are left out of the history list */
export function RunLog({ id }: { id: number }) {
  const [entries, setEntries] = useState<RunLogEntry[] | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    const controller = new AbortController()
    api<RunLogEntry[]>(`history/${id}/log`, { signal: controller.signal })
      .then(setEntries)
      .catch((err: Error) => {
        if (err.name !== 'AbortError') setError(err.message)
      })
    return () => controller.abort()
  }, [id])

  if (error) return <p className="text-xs text-destructive">{error}</p>
  if (!entries)
    return (
      <p className="flex items-center gap-1.5 text-xs text-muted-foreground">
        <Loader2 className="size-3 animate-spin" /> Loading the log…
      </p>
    )

  return (
    <ol
      className="max-h-80 space-y-0.5 overflow-y-auto rounded-md bg-muted p-2 font-mono text-xs"
      aria-label="Run log"
    >
      {entries.map((e, i) => (
        <li key={i} className="flex gap-2 whitespace-pre-wrap break-words">
          <span className="shrink-0 text-muted-foreground" title={e.timeUtc}>
            {time.format(new Date(e.timeUtc))}
          </span>
          <span className={cn('w-12 shrink-0', levelTone[e.level])} title={e.level}>
            {e.level === 'Information' ? 'Info' : e.level}
          </span>
          <span className="min-w-0" title={e.category}>
            {e.message}
          </span>
        </li>
      ))}
    </ol>
  )
}
