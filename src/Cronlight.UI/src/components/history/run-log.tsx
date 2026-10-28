import { useEffect, useLayoutEffect, useRef, useState } from 'react'
import { Loader2 } from 'lucide-react'
import { api, ApiError } from '@/api'
import { cn } from '@/lib/utils'
import type { RunLogEntry } from '@/types'

const levelTone: Partial<Record<RunLogEntry['level'], string>> = {
  Warning: 'text-amber-700 dark:text-amber-400',
  Error: 'text-destructive',
  Critical: 'text-destructive',
}

const time = new Intl.DateTimeFormat(undefined, { timeStyle: 'medium' })

/** How often the log of a run going on is read again */
export const LIVE_LOG_MS = 2000

/**
 * What a run logged, loaded when shown: logs are left out of the history list. With `live`, the log of a run going on
 * (api/running/{id}/log), read again every 2 seconds until the server says it is not there: the run has finished, or
 * runs on another node. A read that fails otherwise is tried again; one still waiting is not sent again.
 */
export function RunLog({ path, live = false }: { path: string; live?: boolean }) {
  const [entries, setEntries] = useState<RunLogEntry[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  // No more to read: a saved log loaded or failed, or a live one the server no longer has
  const [ended, setEnded] = useState(false)
  const list = useRef<HTMLOListElement>(null)
  // Whether the reader is at the end of the log, so new lines keep it there
  const atEnd = useRef(true)

  useEffect(() => {
    const controller = new AbortController()
    let reading = false
    let stopped = false
    const read = () => {
      if (reading || stopped || (live && document.hidden)) return
      reading = true
      api<RunLogEntry[]>(path, { signal: controller.signal })
        .then(log => {
          setEntries(log)
          setError(null)
          if (!live) stopped = true
        })
        .catch((err: Error) => {
          if (err.name === 'AbortError') return
          setError(err.message)
          if (!live || (err instanceof ApiError && err.status === 404)) {
            stopped = true
            setEnded(true)
          }
        })
        .finally(() => (reading = false))
    }
    read()
    if (!live) return () => controller.abort()
    const timer = setInterval(read, LIVE_LOG_MS)
    document.addEventListener('visibilitychange', read)
    return () => {
      clearInterval(timer)
      document.removeEventListener('visibilitychange', read)
      controller.abort()
    }
  }, [path, live])

  useLayoutEffect(() => {
    if (live && atEnd.current && list.current) list.current.scrollTop = list.current.scrollHeight
  }, [entries, live])

  if (error && (ended || !entries))
    // A live log that ends says why: usually that the run finished, and its log is in History
    return (
      <p className={cn('text-xs', live && ended ? 'text-muted-foreground' : 'text-destructive')}>
        {error}
      </p>
    )
  if (!entries)
    return (
      <p className="flex items-center gap-1.5 text-xs text-muted-foreground">
        <Loader2 className="size-3 animate-spin" /> Loading the log…
      </p>
    )

  // The last read failed for a moment: what was read before stays, and the next read tries again
  const retrying = error && (
    <p className="text-xs text-destructive">
      Could not read the log just now ({error}); trying again.
    </p>
  )
  if (entries.length === 0)
    return (
      <div className="space-y-1">
        <p className="flex items-center gap-1.5 text-xs text-muted-foreground">
          {live && <Loader2 className="size-3 animate-spin" />} Nothing logged yet.
        </p>
        {retrying}
      </div>
    )

  return (
    <div className="space-y-1">
      <ol
        ref={list}
        className="max-h-80 space-y-0.5 overflow-y-auto rounded-md bg-muted p-2 font-mono text-xs"
        aria-label={live ? 'Run log so far' : 'Run log'}
        onScroll={e => {
          const el = e.currentTarget
          atEnd.current = el.scrollHeight - el.scrollTop - el.clientHeight < 8
        }}
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
      {retrying}
    </div>
  )
}
