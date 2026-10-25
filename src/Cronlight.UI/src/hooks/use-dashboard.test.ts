import { act, renderHook, waitFor } from '@testing-library/react'
import { toast } from 'sonner'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { api } from '@/api'
import { useDashboard } from '@/hooks/use-dashboard'
import { job, jobPage, scheduler } from '@/test/fixtures'
import type { HistorySummary, Job } from '@/types'

vi.mock('@/api', async importOriginal => ({
  ...(await importOriginal<typeof import('@/api')>()),
  api: vi.fn(),
}))
vi.mock('sonner', () => ({ toast: { success: vi.fn(), error: vi.fn() } }))
const mockApi = vi.mocked(api)

const summary: HistorySummary = {
  sinceUtc: '2030-01-01T00:00:00Z',
  succeeded: 3,
  failed: 1,
  vetoed: 0,
  running: 0,
}

/** Answers the dashboard's GETs; a test overrides single paths through `responses`. */
let responses: Record<string, unknown>
const calls = (method = 'GET') =>
  mockApi.mock.calls
    .filter(([, options]) => (options?.method ?? 'GET') === method)
    .map(([path]) => path)
const jobLoads = () => calls().filter(p => p.startsWith('jobs/search?')).length

let hidden = false
Object.defineProperty(document, 'hidden', { configurable: true, get: () => hidden })
function setHidden(value: boolean) {
  hidden = value
  document.dispatchEvent(new Event('visibilitychange'))
}

describe('useDashboard', () => {
  beforeEach(() => {
    responses = {
      jobs: [job()],
      scheduler: scheduler(),
      running: { source: 'local', jobs: [] },
      'history/summary?hours=24': summary,
      'job-types': [],
    }
    mockApi.mockImplementation(async (path, options) => {
      if (options?.method) return undefined
      const search = path.startsWith('jobs/search?')
      const response = responses[search ? 'jobs' : path]
      if (response instanceof Error) throw response
      return search ? jobPage(response as Job[], path) : response
    })
  })
  afterEach(() => {
    vi.useRealTimers()
    setHidden(false)
  })

  it('loads jobs, scheduler, running jobs, job types and the history summary', async () => {
    const { result } = renderHook(() => useDashboard())

    await waitFor(() => expect(result.current.loading).toBe(false))
    expect(result.current.jobs).toEqual([job()])
    expect(result.current.scheduler?.schedulerName).toBe('SampleHost')
    expect(result.current.summary).toEqual(summary)
    expect(result.current.error).toBeNull()
    expect(calls()).toContain('job-types')
  })

  it('does not ask for a summary when history is off', async () => {
    responses.scheduler = scheduler({ historyEnabled: false })

    const { result } = renderHook(() => useDashboard())

    await waitFor(() => expect(result.current.loading).toBe(false))
    expect(result.current.summary).toBeNull()
    expect(calls()).not.toContain('history/summary?hours=24')
  })

  it('shows a load error and clears it once the server answers again', async () => {
    responses.jobs = new Error('Service Unavailable')
    const { result } = renderHook(() => useDashboard())
    await waitFor(() => expect(result.current.error).toBe('Service Unavailable'))
    expect(result.current.loading).toBe(false)

    responses.jobs = [job()]
    await act(() => result.current.refresh())

    expect(result.current.error).toBeNull()
    expect(result.current.jobs).toHaveLength(1)
  })

  it('reports job types that fail to load without blocking the rest', async () => {
    responses['job-types'] = new Error('boom')

    const { result } = renderHook(() => useDashboard())

    await waitFor(() => expect(result.current.loading).toBe(false))
    expect(result.current.error).toBeNull()
    expect(toast.error).toHaveBeenCalledWith('Could not load job types', { description: 'boom' })
  })

  it('asks the server for one page of jobs matching the query, with counts over all of them', async () => {
    responses.jobs = [job({ name: 'a' }), job({ name: 'b', group: 'billing' })]

    const { result } = renderHook(() =>
      useDashboard({ search: ' bill ', type: null, status: 'Active', page: 2 })
    )

    await waitFor(() => expect(result.current.loading).toBe(false))
    expect(calls()).toContain('jobs/search?page=2&pageSize=50&search=bill&status=Active')
    expect(result.current.page?.counts.all).toBe(2)
  })

  it('loads the selected job on its own when it is not on the page', async () => {
    responses['jobs/billing/elsewhere'] = job({ group: 'billing', name: 'elsewhere' })

    const { result } = renderHook(() =>
      useDashboard(undefined, { group: 'billing', name: 'elsewhere' })
    )

    await waitFor(() => expect(result.current.selected?.name).toBe('elsewhere'))
    expect(calls()).toContain('jobs/billing/elsewhere')
  })

  it('does not poll while the page is hidden, and refreshes as soon as it is shown', async () => {
    vi.useFakeTimers()
    renderHook(() => useDashboard())
    await act(() => vi.advanceTimersByTimeAsync(0))
    expect(jobLoads()).toBe(1)

    setHidden(true)
    await act(() => vi.advanceTimersByTimeAsync(60_000))
    expect(jobLoads()).toBe(1)

    await act(async () => setHidden(false))
    await act(() => vi.advanceTimersByTimeAsync(0))
    expect(jobLoads()).toBe(2)
  })

  it('asks for the 24-hour summary at most once a minute', async () => {
    vi.useFakeTimers()
    renderHook(() => useDashboard())
    await act(() => vi.advanceTimersByTimeAsync(0))
    const summaries = () => calls().filter(p => p.startsWith('history/summary')).length
    expect(summaries()).toBe(1)

    await act(() => vi.advanceTimersByTimeAsync(50_000))
    expect(summaries()).toBe(1)
    await act(() => vi.advanceTimersByTimeAsync(10_000))
    expect(summaries()).toBe(2)
  })

  it('refreshes every 10 seconds and stops when unmounted', async () => {
    vi.useFakeTimers()
    const { unmount } = renderHook(() => useDashboard())
    await act(() => vi.advanceTimersByTimeAsync(0))
    expect(jobLoads()).toBe(1)

    await act(() => vi.advanceTimersByTimeAsync(20_000))
    expect(jobLoads()).toBe(3)

    unmount()
    await vi.advanceTimersByTimeAsync(30_000)
    expect(jobLoads()).toBe(3)
  })

  it('runs an action on the job’s escaped path, confirms it and refreshes', async () => {
    const { result } = renderHook(() => useDashboard())
    await waitFor(() => expect(result.current.loading).toBe(false))
    mockApi.mockClear()

    const ok = await act(() =>
      result.current.actions.run(job({ group: 'a/b', name: 'x y' }), 'pause')
    )

    expect(ok).toBe(true)
    expect(mockApi).toHaveBeenCalledWith('jobs/a%2Fb/x%20y/pause', { method: 'POST' })
    expect(toast.success).toHaveBeenCalledWith('x y: paused')
    expect(jobLoads()).toBe(1)
  })

  it('looks again a second after triggering a job, once the scheduler has picked it up', async () => {
    vi.useFakeTimers()
    const { result } = renderHook(() => useDashboard())
    await act(() => vi.advanceTimersByTimeAsync(0))
    mockApi.mockClear()

    await act(() => result.current.actions.run(job(), 'trigger'))
    expect(jobLoads()).toBe(1)

    await act(() => vi.advanceTimersByTimeAsync(1000))
    expect(jobLoads()).toBe(2)
  })

  it('reports a failed action and resolves to false', async () => {
    const { result } = renderHook(() => useDashboard())
    await waitFor(() => expect(result.current.loading).toBe(false))
    mockApi.mockRejectedValueOnce(new Error('Job is not in this app'))

    const ok = await act(() => result.current.actions.remove(job()))

    expect(ok).toBe(false)
    expect(toast.error).toHaveBeenCalledWith('Job is not in this app')
    expect(toast.success).not.toHaveBeenCalled()
  })

  it('creates, updates and deletes with the right method and body', async () => {
    const { result } = renderHook(() => useDashboard())
    await waitFor(() => expect(result.current.loading).toBe(false))
    const request = {
      type: 'Acme.Jobs.NightlyJob',
      name: 'nightly',
      group: null,
      description: null,
      data: {},
      triggers: [],
      retry: { maxRetries: 0, delaySeconds: 60, exponential: false },
    }
    const update = {
      description: 'new',
      data: {},
      triggers: [],
      retry: { maxRetries: 2, delaySeconds: 10, exponential: false },
    }

    await act(async () => {
      await result.current.actions.create(request)
      await result.current.actions.update(job(), update)
      await result.current.actions.remove(job())
    })

    expect(mockApi).toHaveBeenCalledWith('jobs', { method: 'POST', body: request })
    expect(mockApi).toHaveBeenCalledWith('jobs/DEFAULT/nightly', { method: 'PUT', body: update })
    expect(mockApi).toHaveBeenCalledWith('jobs/DEFAULT/nightly', { method: 'DELETE' })
    expect(vi.mocked(toast.success).mock.calls.map(([message]) => message)).toEqual([
      'nightly created',
      'nightly updated',
      'nightly deleted',
    ])
  })

  it('runs a bulk action on every job and counts the failures', async () => {
    const { result } = renderHook(() => useDashboard())
    await waitFor(() => expect(result.current.loading).toBe(false))
    const targets = [job({ name: 'a' }), job({ name: 'b' }), job({ name: 'c' })]

    await act(() => result.current.actions.bulk('resume', targets))
    expect(toast.success).toHaveBeenCalledWith('3 jobs resumed')

    mockApi.mockImplementation(async (path, options) => {
      if (path === 'jobs/DEFAULT/b/pause') throw new Error('Trigger is locked')
      if (options?.method) return undefined
      return path.startsWith('jobs/search?')
        ? jobPage(responses.jobs as Job[], path)
        : responses[path]
    })
    await act(() => result.current.actions.bulk('pause', targets))

    expect(calls('POST').filter(p => p.endsWith('/pause'))).toHaveLength(3)
    expect(toast.error).toHaveBeenCalledWith('1 of 3 failed', { description: 'Trigger is locked' })
  })
})
