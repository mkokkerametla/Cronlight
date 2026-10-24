import { useCallback, useEffect, useState } from 'react'
import { api } from '@/api'
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

  const loadFirstPage = useCallback(async () => {
    try {
      const page = await api<HistoryPage>(
        query(
          { status, job: group !== undefined && name !== undefined ? { group, name } : null },
          null,
          limit
        )
      )
      setRecords(page.records)
      setNextBefore(page.nextBefore)
      setPages(1)
      setError(null)
    } catch (err) {
      setError((err as Error).message)
    } finally {
      setLoaded(true)
    }
  }, [status, group, name, limit])

  useEffect(() => {
    void loadFirstPage()
  }, [loadFirstPage])

  // Only auto-refresh while just the first page is shown, so "load more" results don't jump around
  useEffect(() => {
    if (pages > 1) return
    const interval = setInterval(loadFirstPage, POLL_MS)
    return () => clearInterval(interval)
  }, [loadFirstPage, pages])

  const loadMore = useCallback(async () => {
    if (nextBefore === null) return
    try {
      const page = await api<HistoryPage>(query(filter, nextBefore, limit))
      setRecords(prev => [...prev, ...page.records])
      setNextBefore(page.nextBefore)
      setPages(p => p + 1)
    } catch (err) {
      setError((err as Error).message)
    }
  }, [filter, nextBefore, limit])

  return { records, hasMore: nextBefore !== null, loadMore, refresh: loadFirstPage, error, loaded }
}
