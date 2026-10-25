import { act, renderHook, waitFor } from '@testing-library/react'
import { toast } from 'sonner'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { api } from '@/api'
import { useDashboard } from '@/hooks/use-dashboard'
import { job, scheduler } from '@/test/fixtures'
import type { HistorySummary } from '@/types'

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
      const response = responses[path]
      if (response instanceof Error) throw response
      return response
    })
  })
  afterEach(() => {
    vi.useRealTimers()
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

  it('refreshes every 10 seconds and stops when unmounted', async () => {
    vi.useFakeTimers()
    const { unmount } = renderHook(() => useDashboard())
    await act(() => vi.advanceTimersByTimeAsync(0))
    const jobLoads = () => calls().filter(p => p === 'jobs').length
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
    expect(calls()).toContain('jobs')
  })

  it('looks again a second after triggering a job, once the scheduler has picked it up', async () => {
    vi.useFakeTimers()
    const { result } = renderHook(() => useDashboard())
    await act(() => vi.advanceTimersByTimeAsync(0))
    mockApi.mockClear()

    await act(() => result.current.actions.run(job(), 'trigger'))
    expect(calls().filter(p => p === 'jobs')).toHaveLength(1)

    await act(() => vi.advanceTimersByTimeAsync(1000))
    expect(calls().filter(p => p === 'jobs')).toHaveLength(2)
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
    }
    const update = { description: 'new', data: {}, triggers: [] }

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
      return options?.method ? undefined : responses[path]
    })
    await act(() => result.current.actions.bulk('pause', targets))

    expect(calls('POST').filter(p => p.endsWith('/pause'))).toHaveLength(3)
    expect(toast.error).toHaveBeenCalledWith('1 of 3 failed', { description: 'Trigger is locked' })
  })
})
