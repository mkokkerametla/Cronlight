import { useCallback, useEffect, useRef, useState } from 'react'
import { api } from '@/api'
import { usePolling, useRefreshInterval } from '@/hooks/use-polling'
import type { ExecutionRecord, HistoryFilter, HistoryPage } from '@/types'

const PAGE_SIZE = 50

function query(filter: HistoryFilter, before: number | null, limit: number) {
  const params = new URLSearchParams({ limit: String(limit) })
  if (filter.status !== 'all') params.set('status', filter.status)
  if (filter.job) {
    params.set('group', filter.job.group)
    params.set('name', filter.job.name)
  }
  if (before !== null) params.set('before', String(before))
  return `history?${params}`
}

/**
 * Newest-first execution history with keyset "load more". The first page refreshes while it is the only page.
 * While held (the pointer is over the list), a refresh updates the runs shown in place and keeps new ones aside,
 * counted in `newRuns`, so nothing moves under the pointer.
 */
export function useHistory(filter: HistoryFilter, limit = PAGE_SIZE) {
  const [records, setRecords] = useState<ExecutionRecord[]>([])
  const [nextBefore, setNextBefore] = useState<number | null>(null)
  const [pages, setPages] = useState(1)
  const [error, setError] = useState<string | null>(null)
  const [loaded, setLoaded] = useState(false)
  // The newest first page while new runs in it are kept aside, and how many there are
  const [held, setHeld] = useState<{ page: HistoryPage; newRuns: number } | null>(null)
  const refreshMs = useRefreshInterval()
  const { status, job } = filter
  const group = job?.group
  const name = job?.name

  // Every load takes a new generation and applies its response only if no newer load started meanwhile,
  // so a slow response for an old filter, or a poll racing "load more", cannot overwrite newer state
  const generation = useRef(0)
  const loadingMore = useRef(false)
  const holding = useRef(false)
  // The runs on screen, for a refresh to compare with
  const shown = useRef<ExecutionRecord[]>([])

  const show = useCallback((rows: ExecutionRecord[]) => {
    shown.current = rows
    setRecords(rows)
  }, [])

  const showFirstPage = useCallback(
    (page: HistoryPage) => {
      show(page.records)
      setNextBefore(page.nextBefore)
      setPages(1)
      setHeld(null)
    },
    [show]
  )

  // A refresh (a poll, or looking again after a run) keeps new runs aside while held; a new filter never does
  const loadFirstPage = useCallback(
    async (refresh = false) => {
      const current = ++generation.current
      try {
        const page = await api<HistoryPage>(
          query(
            { status, job: group !== undefined && name !== undefined ? { group, name } : null },
            null,
            limit
          )
        )
        if (current !== generation.current) return
        const listed = new Set(shown.current.map(r => r.id))
        const newRuns = page.records.filter(r => !listed.has(r.id)).length
        if (refresh && holding.current && listed.size > 0 && newRuns > 0) {
          const latest = new Map(page.records.map(r => [r.id, r]))
          show(shown.current.map(r => latest.get(r.id) ?? r))
          setHeld({ page, newRuns })
        } else showFirstPage(page)
        setError(null)
      } catch (err) {
        if (current === generation.current) setError((err as Error).message)
      } finally {
        if (current === generation.current) setLoaded(true)
      }
    },
    [status, group, name, limit, show, showFirstPage]
  )

  useEffect(() => {
    void loadFirstPage()
  }, [loadFirstPage])

  const refresh = useCallback(() => void loadFirstPage(true), [loadFirstPage])

  // Only auto-refresh while just the first page is shown, so "load more" results don't jump around
  usePolling(
    () => {
      if (!loadingMore.current) refresh()
    },
    refreshMs,
    pages === 1
  )

  const showNewRuns = useCallback(() => {
    if (held) showFirstPage(held.page)
  }, [held, showFirstPage])

  // Ending the hold shows the runs kept aside
  const holdNewRuns = useCallback(
    (hold: boolean) => {
      holding.current = hold
      if (!hold) showNewRuns()
    },
    [showNewRuns]
  )

  const loadMore = useCallback(async () => {
    if (nextBefore === null || loadingMore.current) return
    const current = ++generation.current
    loadingMore.current = true
    try {
      const page = await api<HistoryPage>(query(filter, nextBefore, limit))
      if (current !== generation.current) return
      show([...shown.current, ...page.records])
      setNextBefore(page.nextBefore)
      setPages(p => p + 1)
    } catch (err) {
      if (current === generation.current) setError((err as Error).message)
    } finally {
      loadingMore.current = false
    }
  }, [filter, nextBefore, limit, show])

  return {
    records,
    hasMore: nextBefore !== null,
    loadMore,
    refresh,
    error,
    loaded,
    newRuns: held?.newRuns ?? 0,
    showNewRuns,
    holdNewRuns,
  }
}
