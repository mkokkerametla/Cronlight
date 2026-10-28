import { useEffect, useState } from 'react'
import { api } from '@/api'
import { usePolling, useRefreshInterval } from '@/hooks/use-polling'
import type { HistoryStats } from '@/types'

/**
 * How often the figures are counted again: less often than the lists refresh, since each count reads every run in the
 * period, and figures over hours or days hardly move in seconds
 */
export const STATS_REFRESH_MS = 30_000

/** Run statistics over the last `hours`, for one job or every job */
export function useRunStats(job: { group: string; name: string } | null, hours: number) {
  const [answer, setAnswer] = useState<{ path: string; stats: HistoryStats } | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [polls, setPolls] = useState(0)
  const refreshMs = useRefreshInterval()
  usePolling(() => setPolls(n => n + 1), Math.max(refreshMs, STATS_REFRESH_MS))

  const params = new URLSearchParams({ hours: String(hours) })
  if (job) {
    params.set('group', job.group)
    params.set('name', job.name)
  }
  const path = `history/stats?${params}`

  useEffect(() => {
    const controller = new AbortController()
    api<HistoryStats>(path, { signal: controller.signal })
      .then(stats => {
        setAnswer({ path, stats })
        setError(null)
      })
      .catch((err: Error) => {
        if (err.name !== 'AbortError') setError(err.message)
      })
    return () => controller.abort()
  }, [path, polls])

  // Figures for another job or period are not shown while these load
  return { stats: answer?.path === path ? answer.stats : null, error }
}
