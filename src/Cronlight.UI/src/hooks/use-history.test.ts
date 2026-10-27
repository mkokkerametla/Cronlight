import { act, renderHook, waitFor } from '@testing-library/react'
import { createElement, type ReactNode } from 'react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { api } from '@/api'
import { useHistory } from '@/hooks/use-history'
import { RefreshInterval } from '@/hooks/use-polling'
import { execution } from '@/test/fixtures'
import type { ExecutionRecord, ExecutionStatus, HistoryFilter, HistoryPage } from '@/types'

vi.mock('@/api', () => ({ api: vi.fn() }))
const mockApi = vi.mocked(api)

const page = (
  ids: number[],
  nextBefore: number | null = null,
  status?: ExecutionStatus
): HistoryPage => ({
  records: ids.map(id => execution(id, status && { status })),
  nextBefore,
})

/** A response the test resolves when it chooses, to arrange races. */
function deferred<T>() {
  let resolve!: (value: T) => void
  const promise = new Promise<T>(r => (resolve = r))
  return { promise, resolve }
}

const all: HistoryFilter = { status: 'all', job: null }
const ids = (records: ExecutionRecord[]) => records.map(r => r.id)

describe('useHistory', () => {
  beforeEach(() => {
    mockApi.mockReset()
  })
  afterEach(() => {
    vi.useRealTimers()
  })

  it('loads the first page for the filter', async () => {
    mockApi.mockResolvedValue(page([3, 2, 1]))

    const { result } = renderHook(() =>
      useHistory({ status: 'Failed', job: { group: 'billing', name: 'invoices' } })
    )

    await waitFor(() => expect(result.current.loaded).toBe(true))
    expect(ids(result.current.records)).toEqual([3, 2, 1])
    expect(result.current.hasMore).toBe(false)
    const [path] = mockApi.mock.calls[0] as [string]
    const params = new URLSearchParams(path.split('?')[1])
    expect(Object.fromEntries(params)).toEqual({
      limit: '50',
      status: 'Failed',
      group: 'billing',
      name: 'invoices',
    })
  })

  it('reports a failed load as an error', async () => {
    mockApi.mockRejectedValue(new Error('history unavailable'))

    const { result } = renderHook(() => useHistory(all))

    await waitFor(() => expect(result.current.error).toBe('history unavailable'))
    expect(result.current.loaded).toBe(true)
  })

  it('ignores a slow response for a filter that is no longer selected', async () => {
    const slowAll = deferred<HistoryPage>()
    mockApi.mockImplementation(path =>
      (path as string).includes('status=Failed')
        ? Promise.resolve(page([9], null, 'Failed'))
        : slowAll.promise
    )
    const { result, rerender } = renderHook(({ filter }) => useHistory(filter), {
      initialProps: { filter: all },
    })

    rerender({ filter: { status: 'Failed', job: null } })
    await waitFor(() => expect(ids(result.current.records)).toEqual([9]))
    await act(async () => slowAll.resolve(page([3, 2, 1])))

    expect(ids(result.current.records)).toEqual([9])
  })

  it('loads more with the keyset cursor and appends the next page', async () => {
    mockApi.mockImplementation(path =>
      Promise.resolve((path as string).includes('before=4') ? page([3, 2]) : page([5, 4], 4))
    )
    const { result } = renderHook(() => useHistory(all))
    await waitFor(() => expect(result.current.hasMore).toBe(true))

    await act(() => result.current.loadMore())

    expect(ids(result.current.records)).toEqual([5, 4, 3, 2])
    expect(result.current.hasMore).toBe(false)
  })

  it('refreshes every 10 seconds while only the first page is shown', async () => {
    vi.useFakeTimers()
    mockApi.mockImplementation(path =>
      Promise.resolve((path as string).includes('before=') ? page([3]) : page([5, 4], 4))
    )
    const { result } = renderHook(() => useHistory(all))
    await act(() => vi.advanceTimersByTimeAsync(0))
    expect(mockApi).toHaveBeenCalledTimes(1)

    await act(() => vi.advanceTimersByTimeAsync(10_000))
    expect(mockApi).toHaveBeenCalledTimes(2)

    // Once more pages are loaded, polling stops so the list does not jump around
    await act(() => result.current.loadMore())
    await act(() => vi.advanceTimersByTimeAsync(30_000))
    expect(mockApi).toHaveBeenCalledTimes(3)
    expect(ids(result.current.records)).toEqual([5, 4, 3])
  })

  it("refreshes at the server's interval when App provides one", async () => {
    vi.useFakeTimers()
    mockApi.mockResolvedValue(page([1]))
    const wrapper = ({ children }: { children: ReactNode }) =>
      createElement(RefreshInterval.Provider, { value: 1000 }, children)
    renderHook(() => useHistory(all), { wrapper })
    await act(() => vi.advanceTimersByTimeAsync(0))

    await act(() => vi.advanceTimersByTimeAsync(3000))
    expect(mockApi).toHaveBeenCalledTimes(4)
  })

  it('skips a poll that fires while "load more" is loading, so the extra rows survive', async () => {
    vi.useFakeTimers()
    const nextPage = deferred<HistoryPage>()
    mockApi.mockImplementation(path =>
      (path as string).includes('before=') ? nextPage.promise : Promise.resolve(page([5, 4], 4))
    )
    const { result } = renderHook(() => useHistory(all))
    await act(() => vi.advanceTimersByTimeAsync(0))

    let loading!: Promise<void>
    act(() => {
      loading = result.current.loadMore()
    })
    await act(() => vi.advanceTimersByTimeAsync(10_000))
    expect(mockApi).toHaveBeenCalledTimes(2) // first page and "load more"; the poll was skipped

    await act(async () => {
      nextPage.resolve(page([3, 2]))
      await loading
    })
    expect(ids(result.current.records)).toEqual([5, 4, 3, 2])
  })

  it('asks for older runs once while they load, however often "load more" is pressed', async () => {
    const nextPage = deferred<HistoryPage>()
    mockApi.mockImplementation(path =>
      (path as string).includes('before=') ? nextPage.promise : Promise.resolve(page([5, 4], 4))
    )
    const { result } = renderHook(() => useHistory(all))
    await waitFor(() => expect(result.current.hasMore).toBe(true))

    let loading!: Promise<void>
    act(() => {
      loading = result.current.loadMore()
      void result.current.loadMore()
    })
    await act(async () => {
      nextPage.resolve(page([3, 2]))
      await loading
    })

    expect(mockApi).toHaveBeenCalledTimes(2)
    expect(ids(result.current.records)).toEqual([5, 4, 3, 2])
  })

  it('reports older runs that fail to load, keeping the runs shown', async () => {
    mockApi.mockImplementation(path =>
      (path as string).includes('before=')
        ? Promise.reject(new Error('history unavailable'))
        : Promise.resolve(page([5, 4], 4))
    )
    const { result } = renderHook(() => useHistory(all))
    await waitFor(() => expect(result.current.hasMore).toBe(true))

    await act(() => result.current.loadMore())

    expect(result.current.error).toBe('history unavailable')
    expect(ids(result.current.records)).toEqual([5, 4])
    expect(result.current.hasMore).toBe(true)
  })

  it('drops older runs that arrive after another filter was chosen', async () => {
    const olderAll = deferred<HistoryPage>()
    mockApi.mockImplementation(path => {
      if ((path as string).includes('status=Failed'))
        return Promise.resolve(page([9], null, 'Failed'))
      return (path as string).includes('before=')
        ? olderAll.promise
        : Promise.resolve(page([5, 4], 4))
    })
    const { result, rerender } = renderHook(({ filter }) => useHistory(filter), {
      initialProps: { filter: all },
    })
    await waitFor(() => expect(result.current.hasMore).toBe(true))

    let loading!: Promise<void>
    act(() => {
      loading = result.current.loadMore()
    })
    rerender({ filter: { status: 'Failed', job: null } })
    await waitFor(() => expect(ids(result.current.records)).toEqual([9]))
    await act(async () => {
      olderAll.resolve(page([3, 2]))
      await loading
    })

    expect(ids(result.current.records)).toEqual([9])
  })

  describe('held', () => {
    const first: HistoryPage = {
      records: [execution(2, { status: 'Running' }), execution(1)],
      nextBefore: null,
    }
    const later: HistoryPage = {
      records: [execution(3), execution(2, { status: 'Succeeded' }), execution(1)],
      nextBefore: null,
    }

    async function holdThenPoll(start = first, next = later) {
      vi.useFakeTimers()
      mockApi.mockResolvedValueOnce(start).mockResolvedValue(next)
      const hook = renderHook(({ filter }) => useHistory(filter), { initialProps: { filter: all } })
      await act(() => vi.advanceTimersByTimeAsync(0))
      act(() => hook.result.current.holdNewRuns(true))
      await act(() => vi.advanceTimersByTimeAsync(10_000))
      return hook
    }

    it('keeps new runs aside, and updates the runs shown in place', async () => {
      const { result } = await holdThenPoll()

      // Nothing moves: the run that finished shows it where it is, and the new one waits
      expect(ids(result.current.records)).toEqual([2, 1])
      expect(result.current.records[0].status).toBe('Succeeded')
      expect(result.current.newRuns).toBe(1)

      act(() => result.current.showNewRuns())
      expect(ids(result.current.records)).toEqual([3, 2, 1])
      expect(result.current.newRuns).toBe(0)
    })

    it('counts the runs of every refresh while held, still without moving the runs shown', async () => {
      const { result } = await holdThenPoll()
      mockApi.mockResolvedValue({ records: [execution(4), ...later.records], nextBefore: null })

      await act(() => vi.advanceTimersByTimeAsync(10_000))

      expect(ids(result.current.records)).toEqual([2, 1])
      expect(result.current.newRuns).toBe(2)
      act(() => result.current.showNewRuns())
      expect(ids(result.current.records)).toEqual([4, 3, 2, 1])
    })

    it('shows the runs kept aside once the hold ends', async () => {
      const { result } = await holdThenPoll()

      act(() => result.current.holdNewRuns(false))

      expect(ids(result.current.records)).toEqual([3, 2, 1])
      expect(result.current.newRuns).toBe(0)
    })

    it('does not hold back a filter just chosen', async () => {
      const { result, rerender } = await holdThenPoll()
      mockApi.mockResolvedValue(page([9], null, 'Failed'))

      rerender({ filter: { status: 'Failed', job: null } })
      await act(() => vi.advanceTimersByTimeAsync(0))

      expect(ids(result.current.records)).toEqual([9])
      expect(result.current.newRuns).toBe(0)
    })

    it('does not offer the runs kept aside for the last filter while the new one loads', async () => {
      const { result, rerender } = await holdThenPoll()
      mockApi.mockReturnValue(deferred<HistoryPage>().promise)

      rerender({ filter: { status: 'Failed', job: null } })
      expect(result.current.newRuns).toBe(0)
      act(() => result.current.holdNewRuns(false))

      expect(ids(result.current.records)).toEqual([2, 1])
    })

    it('counts as new only runs newer than those shown, and marks runs no longer listed', async () => {
      // Under the Running filter: 5 finishes and leaves the page, older 2 comes into it, and 6 starts
      const { result } = await holdThenPoll(
        page([5, 3], null, 'Running'),
        page([6, 3, 2], null, 'Running')
      )

      expect(ids(result.current.records)).toEqual([5, 3])
      expect(result.current.newRuns).toBe(1)
      expect([...result.current.gone]).toEqual([5])

      act(() => result.current.holdNewRuns(false))
      expect(ids(result.current.records)).toEqual([6, 3, 2])
      expect(result.current.gone.size).toBe(0)
    })

    it('keeps a run that leaves the page in its place, without new runs, until the hold ends', async () => {
      const { result } = await holdThenPoll(
        page([2, 1], null, 'Running'),
        page([1], null, 'Running')
      )

      expect(ids(result.current.records)).toEqual([2, 1])
      expect(result.current.newRuns).toBe(0)
      expect([...result.current.gone]).toEqual([2])

      act(() => result.current.holdNewRuns(false))
      expect(ids(result.current.records)).toEqual([1])
    })

    it('keeps the runs "load more" added when it shows the runs kept aside', async () => {
      // 2 moves past the end of the first page as 4 starts: it is further down, not gone
      const { result } = await holdThenPoll(page([3, 2], 2), page([4, 3], 3))
      expect(result.current.gone.size).toBe(0)
      mockApi.mockResolvedValue(page([1], null))

      await act(() => result.current.loadMore())
      expect(ids(result.current.records)).toEqual([3, 2, 1])
      act(() => result.current.holdNewRuns(false))

      expect(ids(result.current.records)).toEqual([4, 3, 2, 1])
      expect(result.current.hasMore).toBe(false)
    })

    it('drops the runs "load more" added when the runs kept aside are all there are', async () => {
      // Under the Running filter: 2 finishes and leaves, and the first page now holds every running run
      const { result } = await holdThenPoll(
        page([3, 2], 2, 'Running'),
        page([4, 3], null, 'Running')
      )
      mockApi.mockResolvedValue(page([1], null, 'Running'))

      await act(() => result.current.loadMore())
      expect(ids(result.current.records)).toEqual([3, 2, 1])
      act(() => result.current.holdNewRuns(false))

      expect(ids(result.current.records)).toEqual([4, 3])
      expect(result.current.hasMore).toBe(false)
    })

    it('shows a run asked for a second later, without keeping it aside', async () => {
      const { result } = await holdThenPoll(first, first)
      mockApi.mockResolvedValue(later)

      act(() => result.current.showRunSoon())
      await act(() => vi.advanceTimersByTimeAsync(1000))

      expect(ids(result.current.records)).toEqual([3, 2, 1])
      expect(result.current.newRuns).toBe(0)
    })

    it('shows a run asked for at the next refresh when history has not recorded it a second later', async () => {
      const { result } = await holdThenPoll(first, first)

      act(() => result.current.showRunSoon())
      await act(() => vi.advanceTimersByTimeAsync(1000))
      mockApi.mockResolvedValue(later)
      await act(() => vi.advanceTimersByTimeAsync(10_000))

      expect(ids(result.current.records)).toEqual([3, 2, 1])
      expect(result.current.newRuns).toBe(0)
    })

    it('calls off every look asked for once it is gone, even one asked for after', async () => {
      const { result, unmount } = await holdThenPoll(first, first)
      const calls = () => mockApi.mock.calls.length

      act(() => result.current.showRunSoon())
      await act(() => vi.advanceTimersByTimeAsync(500))
      act(() => result.current.showRunSoon())
      const before = calls()
      unmount()
      result.current.showRunSoon()
      await act(() => vi.advanceTimersByTimeAsync(2000))

      expect(calls()).toBe(before)
    })
  })
})
