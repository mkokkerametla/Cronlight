import { useEffect, useRef, useState } from 'react'
import { CircleAlert, CircleCheck, Loader2 } from 'lucide-react'
import { api } from '@/api'
import { formatDateTime, formatRelative } from '@/lib/format'
import type { CronPreview as Preview } from '@/types'

const MIN_LENGTH = 6

export function CronPreview({
  expression,
  timeZone,
  onInvalidChange,
}: {
  expression: string
  timeZone: string
  /** Told whether the expression is shown as invalid, so the form can refuse to save it */
  onInvalidChange?: (invalid: boolean) => void
}) {
  const [preview, setPreview] = useState<Preview | null>(null)
  const trimmed = expression.trim()
  const zone = timeZone.trim()
  const invalid =
    trimmed.length >= MIN_LENGTH && preview?.expression === trimmed && !preview.isValid

  // The latest callback, so a caller passing a new function each render is not told again
  const report = useRef(onInvalidChange)
  useEffect(() => {
    report.current = onInvalidChange
  })
  useEffect(() => {
    report.current?.(invalid)
  }, [invalid])
  // A removed trigger no longer holds the form back
  useEffect(() => () => report.current?.(false), [])

  useEffect(() => {
    if (trimmed.length < MIN_LENGTH) return
    const controller = new AbortController()
    const timeout = setTimeout(() => {
      const query = new URLSearchParams({ expression: trimmed, timeZone: zone, count: '3' })
      api<Preview>(`cron/preview?${query}`, { signal: controller.signal })
        .then(setPreview)
        .catch((err: Error) => {
          if (err.name !== 'AbortError')
            setPreview({
              isValid: false,
              expression: trimmed,
              description: null,
              nextFireTimes: [],
              errorMessage: err.message,
            })
        })
    }, 400)
    return () => {
      clearTimeout(timeout)
      controller.abort()
    }
  }, [trimmed, zone])

  if (trimmed.length < MIN_LENGTH) return null

  // Until the debounced request for this exact expression answers, show a checking state
  if (preview?.expression !== trimmed)
    return (
      <p className="flex items-center gap-1.5 text-xs text-muted-foreground">
        <Loader2 className="size-3 animate-spin" /> Checking…
      </p>
    )

  if (!preview.isValid)
    return (
      <p className="flex items-center gap-1.5 text-xs text-destructive">
        <CircleAlert className="size-3.5 shrink-0" /> {preview.errorMessage}
      </p>
    )

  return (
    <div className="space-y-1 text-xs">
      <p className="flex items-center gap-1.5 text-emerald-700 dark:text-emerald-400">
        <CircleCheck className="size-3.5" /> {preview.description ?? 'Valid cron expression'}
      </p>
      <p className="text-muted-foreground">
        Next (your local time):{' '}
        {preview.nextFireTimes.map((t, i) => (
          <span key={t} title={formatRelative(t)}>
            {i > 0 && ' · '}
            {formatDateTime(t)}
          </span>
        ))}
      </p>
    </div>
  )
}
