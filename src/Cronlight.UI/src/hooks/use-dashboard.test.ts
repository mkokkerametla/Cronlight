import { act, renderHook, waitFor } from '@testing-library/react'
import { toast } from 'sonner'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { api } from '@/api'
import { useDashboard } from '@/hooks/use-dashboard'
import { execution, job, jobPage, scheduler, trigger } from '@/test/fixtures'
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

  it('shows the history summary on first load, even when a second refresh overtakes the first', async () => {
    const answer = mockApi.getMockImplementation()!
    const held: Array<() => void> = []
    mockApi.mockImplementation(async (path, options) => {
      if (path.startsWith('jobs/search?')) await new Promise<void>(resolve => held.push(resolve))
      return answer(path, options)
    })
    const { result } = renderHook(() => useDashboard())
    await waitFor(() => expect(held).toHaveLength(1))

    // The tab comes to the front while the first load is under way, which refreshes again
    act(() => setHidden(false))
    await waitFor(() => expect(held).toHaveLength(2))
    // The first load finishes first, and is set aside for the newer one
    await act(async () => held[0]())
    await act(async () => held[1]())

    await waitFor(() => expect(result.current.loading).toBe(false))
    expect(result.current.summary).toEqual(summary)
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
      useDashboard({ search: ' bill ', type: 'NightlyJob', status: 'Active', page: 2 })
    )

    await waitFor(() => expect(result.current.loading).toBe(false))
    expect(calls()).toContain(
      'jobs/search?page=2&pageSize=50&search=bill&type=NightlyJob&status=Active'
    )
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

  it('closes the selected job once it has been deleted', async () => {
    responses['jobs/billing/gone'] = new Error('Not Found')

    const { result } = renderHook(() => useDashboard(undefined, { group: 'billing', name: 'gone' }))

    await waitFor(() => expect(result.current.loading).toBe(false))
    expect(calls()).toContain('jobs/billing/gone')
    expect(result.current.selected).toBeNull()
    expect(result.current.error).toBeNull()
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
    expect(toast.success).toHaveBeenCalledWith(
      'x y: paused',
      expect.objectContaining({ action: expect.objectContaining({ label: 'Undo' }) })
    )
    expect(jobLoads()).toBe(1)
  })

  it('offers to undo a pause, which resumes the job without offering another undo', async () => {
    const { result } = renderHook(() => useDashboard())
    await waitFor(() => expect(result.current.loading).toBe(false))
    mockApi.mockClear()

    await act(() => result.current.actions.run(job(), 'pause'))
    const [, options] = vi.mocked(toast.success).mock.calls.at(-1)!
    const undo = (options as unknown as { action: { onClick: () => void } }).action
    await act(async () => undo.onClick())

    await waitFor(() =>
      expect(mockApi).toHaveBeenCalledWith('jobs/DEFAULT/nightly/resume', { method: 'POST' })
    )
    await waitFor(() =>
      expect(vi.mocked(toast.success).mock.calls.at(-1)).toEqual(['nightly: resumed'])
    )
  })

  it('offers no undo for resuming a job Quartz stopped, since pausing would not stop it again', async () => {
    const { result } = renderHook(() => useDashboard())
    await waitFor(() => expect(result.current.loading).toBe(false))

    await act(() =>
      result.current.actions.run(job({ triggers: [trigger({ state: 'Error' })] }), 'resume')
    )

    expect(vi.mocked(toast.success).mock.calls.at(-1)).toEqual(['nightly: resumed'])
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

  it('runs a job once with different data, and looks again a second later', async () => {
    vi.useFakeTimers()
    const { result } = renderHook(() => useDashboard())
    await act(() => vi.advanceTimersByTimeAsync(0))
    mockApi.mockClear()

    const ok = await act(() => result.current.actions.runWithData(job(), { region: 'us' }))

    expect(ok).toBe(true)
    expect(mockApi).toHaveBeenCalledWith('jobs/DEFAULT/nightly/trigger', {
      method: 'POST',
      body: { data: { region: 'us' } },
    })
    expect(toast.success).toHaveBeenCalledWith('nightly: triggered with different data')
    expect(jobLoads()).toBe(1)
    await act(() => vi.advanceTimersByTimeAsync(1000))
    expect(jobLoads()).toBe(2)
  })

  it('runs a failed run again with its own data, naming the run it retries', async () => {
    vi.useFakeTimers()
    const { result } = renderHook(() => useDashboard())
    await act(() => vi.advanceTimersByTimeAsync(0))
    mockApi.mockClear()

    await act(() =>
      result.current.actions.retry(
        execution(7, { jobGroup: 'a/b', jobName: 'x', fireInstanceId: 'f-7', data: { id: '9' } })
      )
    )
    // A run that used the saved data runs with it again
    await act(() => result.current.actions.retry(execution(8, { fireInstanceId: 'f-8' })))

    expect(calls('POST')).toEqual(['jobs/a%2Fb/x/trigger', 'jobs/DEFAULT/nightly/trigger'])
    expect(mockApi.mock.calls.filter(([, o]) => o?.method).map(([, o]) => o?.body)).toEqual([
      { data: { id: '9' }, retryOf: 'f-7' },
      { data: {}, retryOf: 'f-8' },
    ])
    expect(toast.success).toHaveBeenCalledWith('x: triggered again')
    // A refresh after each, and another a second later once the scheduler has picked them up
    expect(jobLoads()).toBe(2)
    await act(() => vi.advanceTimersByTimeAsync(1000))
    expect(jobLoads()).toBe(4)
  })

  it('does not look again after a run that failed to start', async () => {
    vi.useFakeTimers()
    const { result } = renderHook(() => useDashboard())
    await act(() => vi.advanceTimersByTimeAsync(0))
    mockApi.mockClear()
    mockApi.mockRejectedValueOnce(new Error('Job is not in this app'))
    mockApi.mockRejectedValueOnce(new Error('Job is not in this app'))

    expect(await act(() => result.current.actions.runWithData(job(), {}))).toBe(false)
    expect(await act(() => result.current.actions.retry(execution(1)))).toBe(false)
    await act(() => vi.advanceTimersByTimeAsync(1000))

    expect(jobLoads()).toBe(0)
    expect(toast.error).toHaveBeenCalledTimes(2)
  })

  it('cancels a running job and a waiting retry on their escaped paths', async () => {
    const { result } = renderHook(() => useDashboard())
    await waitFor(() => expect(result.current.loading).toBe(false))

    await act(async () => {
      await result.current.actions.cancel({
        name: 'x y',
        group: 'a/b',
        jobType: null,
        triggerName: 't1',
        triggerGroup: 'DEFAULT',
        fireInstanceId: 'node-1/42',
        instanceName: 'node-1',
        firedTimeUtc: '2030-01-01T00:00:00Z',
        runningForMs: 1000,
      })
      await result.current.actions.cancelRetry(
        job(),
        trigger({ name: 'retry 2', group: 'Cronlight.Retries', kind: 'Retry' })
      )
    })

    expect(calls('POST')).toEqual([
      'jobs/a%2Fb/x%20y/runs/node-1%2F42/cancel',
      'jobs/DEFAULT/nightly/retries/retry%202/cancel-retry',
    ])
    expect(vi.mocked(toast.success).mock.calls.map(([message]) => message)).toEqual([
      'x y: cancellation requested',
      'nightly: retry cancelled',
    ])
  })

  it('pauses and resumes a whole group, or every job in the scheduler', async () => {
    const { result } = renderHook(() => useDashboard())
    await waitFor(() => expect(result.current.loading).toBe(false))

    await act(async () => {
      await result.current.actions.group('a/b', 'pause')
      await result.current.actions.all('resume')
    })

    expect(calls('POST')).toEqual(['groups/a%2Fb/pause', 'scheduler/resume'])
    expect(vi.mocked(toast.success).mock.calls.map(([message]) => message)).toEqual([
      'Group a/b: paused',
      'All jobs resumed',
    ])
  })
})
