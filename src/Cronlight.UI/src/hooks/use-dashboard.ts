import { useCallback, useEffect, useMemo, useState } from 'react'
import { toast } from 'sonner'
import { api, jobPath } from '@/api'
import type {
  CreateJobRequest,
  HistorySummary,
  Job,
  JobAction,
  JobType,
  RunningJobs,
  SchedulerInfo,
  UpdateJobRequest,
} from '@/types'

const POLL_MS = 10_000

export function useDashboard() {
  const [jobs, setJobs] = useState<Job[]>([])
  const [jobTypes, setJobTypes] = useState<JobType[]>([])
  const [scheduler, setScheduler] = useState<SchedulerInfo | null>(null)
  const [running, setRunning] = useState<RunningJobs>({ source: 'local', jobs: [] })
  const [summary, setSummary] = useState<HistorySummary | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)

  const refresh = useCallback(async () => {
    try {
      const [jobsData, schedulerData, runningData] = await Promise.all([
        api<Job[]>('jobs'),
        api<SchedulerInfo>('scheduler'),
        api<RunningJobs>('running'),
      ])
      setJobs(jobsData)
      setScheduler(schedulerData)
      setRunning(runningData)
      setSummary(
        schedulerData.historyEnabled ? await api<HistorySummary>('history/summary?hours=24') : null
      )
      setError(null)
    } catch (err) {
      setError((err as Error).message)
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    void refresh()
    api<JobType[]>('job-types')
      .then(setJobTypes)
      .catch((err: Error) => toast.error('Could not load job types', { description: err.message }))
    const interval = setInterval(refresh, POLL_MS)
    return () => clearInterval(interval)
  }, [refresh])

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

  return { jobs, jobTypes, scheduler, running, summary, error, loading, refresh, actions }
}

export type DashboardActions = ReturnType<typeof useDashboard>['actions']

const pastTense: Record<JobAction, string> = {
  trigger: 'triggered',
  pause: 'paused',
  resume: 'resumed',
}
