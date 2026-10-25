import { useEffect, useRef } from 'react'

/**
 * Calls `poll` every `ms` while the page is visible. Hidden tabs (another tab in front, a minimised
 * window) do not poll, so dashboards left open put no load on the scheduler; showing the page again
 * polls at once, so it never shows stale data.
 */
export function usePolling(poll: () => void, ms: number, enabled = true) {
  const latest = useRef(poll)
  useEffect(() => {
    latest.current = poll
  }, [poll])

  useEffect(() => {
    if (!enabled) return
    const interval = setInterval(() => {
      if (!document.hidden) latest.current()
    }, ms)
    const onVisibilityChange = () => {
      if (!document.hidden) latest.current()
    }
    document.addEventListener('visibilitychange', onVisibilityChange)
    return () => {
      clearInterval(interval)
      document.removeEventListener('visibilitychange', onVisibilityChange)
    }
  }, [ms, enabled])
}
