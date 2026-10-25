import { act, renderHook, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { api } from '@/api'
import { useHistory } from '@/hooks/use-history'
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
})
