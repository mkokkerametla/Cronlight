import { useEffect, useId, useRef, useState } from 'react'
import { CircleAlert, CircleCheck, Loader2 } from 'lucide-react'
import { api } from '@/api'
import { describeCron, formatDateTime, formatRelative } from '@/lib/format'
import { cn } from '@/lib/utils'
import type { CronPreview as Preview } from '@/types'

const MIN_LENGTH = 6

export function CronPreview({
  expression,
  timeZone,
  messageId,
  onInvalidChange,
}: {
  expression: string
  timeZone: string
  /** The id of what it says about the expression, for the field's aria-describedby */
  messageId?: string
  /** Told whether the expression is shown as invalid, so the form can refuse to save it */
  onInvalidChange?: (invalid: boolean) => void
}) {
  const [preview, setPreview] = useState<Preview | null>(null)
  const id = useId()
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
  const checked = preview?.expression === trimmed ? preview : null

  return (
    <div className="space-y-1 text-xs">
      {/* Read out when the check answers, so the reason an expression is refused is heard while typing; the field
          names it as its description too */}
      <p
        id={messageId}
        aria-live="polite"
        className={cn(
          'flex items-center gap-1.5',
          checked?.isValid ? 'text-emerald-700 dark:text-emerald-400' : 'text-destructive'
        )}
      >
        {checked &&
          (checked.isValid ? (
            <>
              <CircleCheck className="size-3.5" />{' '}
              {checked.description ?? describeCron(trimmed) ?? 'Valid cron expression'}
            </>
          ) : (
            <>
              <CircleAlert className="size-3.5 shrink-0" /> {checked.errorMessage}
            </>
          ))}
      </p>
      {!checked && (
        <p className="flex items-center gap-1.5 text-muted-foreground">
          <Loader2 className="size-3 animate-spin" /> Checking…
        </p>
      )}
      {checked?.isValid && (
        // One a line, so each date can be read on its own
        <div className="text-muted-foreground">
          <p id={`${id}-next`}>Next (your local time)</p>
          <ol aria-labelledby={`${id}-next`} className="tabular-nums">
            {checked.nextFireTimes.map(t => (
              <li key={t} title={formatRelative(t)}>
                {formatDateTime(t)}
              </li>
            ))}
          </ol>
        </div>
      )}
    </div>
  )
}
