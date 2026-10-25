import { useCallback, useEffect, useRef, useState } from 'react'
import { api } from '@/api'
import { usePolling } from '@/hooks/use-polling'
import type { ExecutionRecord, HistoryFilter, HistoryPage } from '@/types'

const PAGE_SIZE = 50
const POLL_MS = 10_000

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

/** Newest-first execution history with keyset "load more". The first page refreshes while it is the only page. */
export function useHistory(filter: HistoryFilter, limit = PAGE_SIZE) {
  const [records, setRecords] = useState<ExecutionRecord[]>([])
  const [nextBefore, setNextBefore] = useState<number | null>(null)
  const [pages, setPages] = useState(1)
  const [error, setError] = useState<string | null>(null)
  const [loaded, setLoaded] = useState(false)
  const { status, job } = filter
  const group = job?.group
  const name = job?.name

  // Every load takes a new generation and applies its response only if no newer load started meanwhile,
  // so a slow response for an old filter, or a poll racing "load more", cannot overwrite newer state
  const generation = useRef(0)
  const loadingMore = useRef(false)

  const loadFirstPage = useCallback(async () => {
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
      setRecords(page.records)
      setNextBefore(page.nextBefore)
      setPages(1)
      setError(null)
    } catch (err) {
      if (current === generation.current) setError((err as Error).message)
    } finally {
      if (current === generation.current) setLoaded(true)
    }
  }, [status, group, name, limit])

  useEffect(() => {
    // eslint-disable-next-line react-hooks/set-state-in-effect -- state is set only after the awaited fetch resolves
    void loadFirstPage()
  }, [loadFirstPage])

  // Only auto-refresh while just the first page is shown, so "load more" results don't jump around
  usePolling(
    () => {
      if (!loadingMore.current) void loadFirstPage()
    },
    POLL_MS,
    pages === 1
  )

  const loadMore = useCallback(async () => {
    if (nextBefore === null || loadingMore.current) return
    const current = ++generation.current
    loadingMore.current = true
    try {
      const page = await api<HistoryPage>(query(filter, nextBefore, limit))
      if (current !== generation.current) return
      setRecords(prev => [...prev, ...page.records])
      setNextBefore(page.nextBefore)
      setPages(p => p + 1)
    } catch (err) {
      if (current === generation.current) setError((err as Error).message)
    } finally {
      loadingMore.current = false
    }
  }, [filter, nextBefore, limit])

  return { records, hasMore: nextBefore !== null, loadMore, refresh: loadFirstPage, error, loaded }
}
