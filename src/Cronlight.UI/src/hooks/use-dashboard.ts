import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { toast } from 'sonner'
import { api, jobPath } from '@/api'
import { usePolling } from '@/hooks/use-polling'
import type {
  CreateJobRequest,
  ExecutionRecord,
  HistorySummary,
  Job,
  JobAction,
  JobPage,
  JobQuery,
  JobType,
  RunningJob,
  RunningJobs,
  RunJobRequest,
  SchedulerInfo,
  Trigger,
  UpdateJobRequest,
} from '@/types'

// The 24-hour failure count changes slowly; asking every poll would be a needless query per viewer
const SUMMARY_MS = 60_000
export const PAGE_SIZE = 50

/** The server's refresh interval in milliseconds, or the default until the scheduler has answered */
export const refreshMs = (scheduler: SchedulerInfo | null) =>
  (scheduler?.refreshSeconds ?? 10) * 1000

export const emptyQuery: JobQuery = { search: '', type: null, status: null, page: 1 }

function searchPath(query: JobQuery) {
  const params = new URLSearchParams({ page: String(query.page), pageSize: String(PAGE_SIZE) })
  if (query.search.trim()) params.set('search', query.search.trim())
  if (query.type) params.set('type', query.type)
  if (query.status) params.set('status', query.status)
  return `jobs/search?${params}`
}

/**
 * Everything the dashboard shows, refreshed every RefreshSeconds (10 by default) while the page is visible. The job list is
 * searched, filtered and paged on the server by `query`; the selected job is loaded on its own when it is
 * not on the current page (e.g. opened from history).
 */
export function useDashboard(
  query: JobQuery = emptyQuery,
  selection: Pick<Job, 'group' | 'name'> | null = null
) {
  const [page, setPage] = useState<JobPage | null>(null)
  const [selectedJob, setSelectedJob] = useState<Job | null>(null)
  const [jobTypes, setJobTypes] = useState<JobType[]>([])
  const [scheduler, setScheduler] = useState<SchedulerInfo | null>(null)
  const [running, setRunning] = useState<RunningJobs>({ source: 'local', jobs: [] })
  const [summary, setSummary] = useState<HistorySummary | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)

  const queryKey = JSON.stringify(query)
  const selectionKey = selection && JSON.stringify([selection.group, selection.name])
  // Every refresh takes a new generation and applies its result only if no newer one started meanwhile,
  // so a slow answer for an old search cannot replace a newer page
  const generation = useRef(0)
  const summaryAt = useRef(0)

  const refresh = useCallback(async () => {
    const current = ++generation.current
    const jobQuery = JSON.parse(queryKey) as JobQuery
    const selected = selectionKey && (JSON.parse(selectionKey) as [string, string])
    try {
      const [pageData, schedulerData, runningData] = await Promise.all([
        api<JobPage>(searchPath(jobQuery)),
        api<SchedulerInfo>('scheduler'),
        api<RunningJobs>('running'),
      ])
      // Not on this page: ask for it alone. A job deleted meanwhile answers 404 and closes the sheet.
      const selectedData = selected
        ? (pageData.jobs.find(j => j.group === selected[0] && j.name === selected[1]) ??
          (await api<Job>(jobPath({ group: selected[0], name: selected[1] })).catch(() => null)))
        : null
      let summaryData: HistorySummary | null | undefined
      if (!schedulerData.historyEnabled) summaryData = null
      else if (Date.now() - summaryAt.current >= SUMMARY_MS) {
        summaryData = await api<HistorySummary>('history/summary?hours=24')
        summaryAt.current = Date.now()
      }
      if (current !== generation.current) return
      setPage(pageData)
      setSelectedJob(selectedData)
      setScheduler(schedulerData)
      setRunning(runningData)
      if (summaryData !== undefined) setSummary(summaryData)
      setError(null)
    } catch (err) {
      if (current === generation.current) setError((err as Error).message)
    } finally {
      if (current === generation.current) setLoading(false)
    }
  }, [queryKey, selectionKey])

  useEffect(() => {
    // eslint-disable-next-line react-hooks/set-state-in-effect -- state is set only after the awaited fetch resolves
    void refresh()
  }, [refresh])

  useEffect(() => {
    api<JobType[]>('job-types')
      .then(setJobTypes)
      .catch((err: Error) => toast.error('Could not load job types', { description: err.message }))
  }, [])

  usePolling(() => void refresh(), refreshMs(scheduler))

  // Runs a call, reports the outcome as a toast and refreshes. Resolves to true on success.
  const perform = useCallback(
    async (call: () => Promise<unknown>, success: string) => {
      try {
        await call()
        toast.success(success)
        await refresh()
        return true
      } catch (err) {
        toast.error((err as Error).message)
        return false
      }
    },
    [refresh]
  )

  const actions = useMemo(
    () => ({
      run: async (job: Job, action: JobAction) => {
        const ok = await perform(
          () => api(`${jobPath(job)}/${action}`, { method: 'POST' }),
          `${job.name}: ${pastTense[action]}`
        )
        // The scheduler starts a triggered job asynchronously; look again once it has picked it up
        if (ok && action === 'trigger') setTimeout(refresh, 1000)
        return ok
      },
      runWithData: async (job: Job, data: Record<string, string>) => {
        const body: RunJobRequest = { data }
        const ok = await perform(
          () => api(`${jobPath(job)}/trigger`, { method: 'POST', body }),
          `${job.name}: triggered with different data`
        )
        if (ok) setTimeout(refresh, 1000)
        return ok
      },
      // Runs the job again with the failed run's own data, if it had any. Masked secrets are not kept in history,
      // and the server takes them as the saved values
      retry: async (record: ExecutionRecord) => {
        const body: RunJobRequest = { data: record.data ?? {}, retryOf: record.fireInstanceId }
        const ok = await perform(
          () =>
            api(`${jobPath({ group: record.jobGroup, name: record.jobName })}/trigger`, {
              method: 'POST',
              body,
            }),
          `${record.jobName}: triggered again`
        )
        if (ok) setTimeout(refresh, 1000)
        return ok
      },
      cancel: (run: RunningJob) =>
        perform(
          () =>
            api(`${jobPath(run)}/runs/${encodeURIComponent(run.fireInstanceId)}/cancel`, {
              method: 'POST',
            }),
          `${run.name}: cancellation requested`
        ),
      // A waiting retry is one of the job's triggers, in the Cronlight.Retries group
      cancelRetry: (job: Job, trigger: Trigger) =>
        perform(
          () =>
            api(`${jobPath(job)}/retries/${encodeURIComponent(trigger.name)}/cancel-retry`, {
              method: 'POST',
            }),
          `${job.name}: retry cancelled`
        ),
      remove: (job: Job) =>
        perform(() => api(jobPath(job), { method: 'DELETE' }), `${job.name} deleted`),
      create: (request: CreateJobRequest) =>
        perform(() => api('jobs', { method: 'POST', body: request }), `${request.name} created`),
      update: (job: Job, request: UpdateJobRequest) =>
        perform(() => api(jobPath(job), { method: 'PUT', body: request }), `${job.name} updated`),
      // Quartz has no batch API, so each job is its own call
      bulk: async (action: JobAction, targets: Job[]) => {
        const results = await Promise.allSettled(
          targets.map(job => api(`${jobPath(job)}/${action}`, { method: 'POST' }))
        )
        const failed = results.filter((r): r is PromiseRejectedResult => r.status === 'rejected')
        if (failed.length)
          toast.error(`${failed.length} of ${targets.length} failed`, {
            description: (failed[0].reason as Error).message,
          })
        else toast.success(`${targets.length} jobs ${pastTense[action]}`)
        await refresh()
      },
    }),
    [perform, refresh]
  )

  return {
    jobs: page?.jobs ?? [],
    page,
    selected: selectedJob,
    jobTypes,
    scheduler,
    running,
    summary,
    error,
    loading,
    refresh,
    actions,
  }
}

export type DashboardActions = ReturnType<typeof useDashboard>['actions']

const pastTense: Record<JobAction, string> = {
  trigger: 'triggered',
  pause: 'paused',
  resume: 'resumed',
}
