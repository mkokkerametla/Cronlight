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

/** A refresh kept aside while held: the newest first page, for the filter it was asked with. */
interface Held {
  key: string
  page: HistoryPage
  /** Runs newer than any shown */
  newRuns: number
  /** Runs shown that the page no longer lists, such as a run that finished under the Running filter */
  gone: Set<number>
}

const none = new Set<number>()

/**
 * Newest-first execution history with keyset "load more". The first page refreshes while it is the only page.
 * While held (the pointer is over the list), a refresh changes no row's place: it updates the runs shown where
 * they are, marks those it no longer lists as `gone`, and keeps new runs aside, counted in `newRuns`.
 */
export function useHistory(filter: HistoryFilter, limit = PAGE_SIZE) {
  const [records, setRecords] = useState<ExecutionRecord[]>([])
  const [nextBefore, setNextBefore] = useState<number | null>(null)
  const [pages, setPages] = useState(1)
  const [error, setError] = useState<string | null>(null)
  const [loaded, setLoaded] = useState(false)
  const [kept, setKept] = useState<Held | null>(null)
  const refreshMs = useRefreshInterval()
  const { status, job } = filter
  const group = job?.group
  const name = job?.name
  const firstPage = query(
    { status, job: group !== undefined && name !== undefined ? { group, name } : null },
    null,
    limit
  )
  // Runs kept aside for another filter are not shown under this one
  const held = kept?.key === firstPage ? kept : null

  // Every load takes a new generation and applies its response only if no newer load started meanwhile,
  // so a slow response for an old filter, or a poll racing "load more", cannot overwrite newer state
  const generation = useRef(0)
  const loadingMore = useRef(false)
  const holding = useRef(false)
  // The runs on screen, for a refresh to compare with
  const shown = useRef<ExecutionRecord[]>([])
  const lookAgain = useRef<ReturnType<typeof setTimeout>>(undefined)
  const mounted = useRef(true)
  // Until when a run asked for is shown rather than kept aside, should history record it after the look again
  const runAskedUntil = useRef(0)

  const show = useCallback((rows: ExecutionRecord[]) => {
    shown.current = rows
    setRecords(rows)
  }, [])

  const showFirstPage = useCallback(
    (page: HistoryPage) => {
      show(page.records)
      setNextBefore(page.nextBefore)
      setPages(1)
      setKept(null)
    },
    [show]
  )

  // A poll keeps the rows in place while held; a new filter, or looking again after a run, never does
  const loadFirstPage = useCallback(
    async (poll = false) => {
      const current = ++generation.current
      try {
        const page = await api<HistoryPage>(firstPage)
        if (current !== generation.current) return
        const rows = shown.current
        const moved =
          page.records.length !== rows.length || page.records.some((r, i) => r.id !== rows[i].id)
        const newer = rows.length > 0 && (page.records[0]?.id ?? 0) > rows[0].id
        const asked = newer && Date.now() < runAskedUntil.current
        if (newer) runAskedUntil.current = 0
        if (poll && holding.current && rows.length > 0 && moved && !asked) {
          const latest = new Map(page.records.map(r => [r.id, r]))
          // Runs older than the page reaches are past its end, not gone
          const oldest = page.records.at(-1)?.id ?? Infinity
          const listed = (id: number) => page.nextBefore === null || id >= oldest
          show(rows.map(r => latest.get(r.id) ?? r))
          setKept({
            key: firstPage,
            page,
            newRuns: page.records.filter(r => r.id > rows[0].id).length,
            gone: new Set(rows.filter(r => !latest.has(r.id) && listed(r.id)).map(r => r.id)),
          })
        } else showFirstPage(page)
        setError(null)
      } catch (err) {
        if (current === generation.current) setError((err as Error).message)
      } finally {
        if (current === generation.current) setLoaded(true)
      }
    },
    [firstPage, show, showFirstPage]
  )

  useEffect(() => {
    void loadFirstPage()
  }, [loadFirstPage])

  useEffect(() => {
    mounted.current = true
    return () => {
      mounted.current = false
      clearTimeout(lookAgain.current)
    }
  }, [])

  // Looks again a second after a run is started, to show it: a run asked for is not kept aside
  const showRunSoon = useCallback(() => {
    if (!mounted.current) return
    runAskedUntil.current = Date.now() + 15_000
    clearTimeout(lookAgain.current)
    lookAgain.current = setTimeout(() => void loadFirstPage(), 1000)
  }, [loadFirstPage])

  // Only auto-refresh while just the first page is shown, so "load more" results don't jump around
  usePolling(
    () => {
      if (!loadingMore.current) void loadFirstPage(true)
    },
    refreshMs,
    pages === 1
  )

  // Shows what was kept aside, keeping any older runs "load more" added below it
  const showNewRuns = useCallback(() => {
    if (!held) return
    if (pages === 1) return showFirstPage(held.page)
    const oldest = held.page.records.at(-1)?.id
    const older =
      held.page.nextBefore === null || oldest === undefined
        ? []
        : shown.current.filter(r => r.id < oldest)
    show([...held.page.records, ...older])
    if (older.length === 0) {
      setNextBefore(held.page.nextBefore)
      setPages(1)
    }
    setKept(null)
  }, [held, pages, show, showFirstPage])

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
    showRunSoon,
    error,
    loaded,
    newRuns: held?.newRuns ?? 0,
    gone: held?.gone ?? none,
    showNewRuns,
    holdNewRuns,
  }
}
