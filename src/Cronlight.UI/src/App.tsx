import { useState } from 'react'
import { RotateCw, WifiOff } from 'lucide-react'
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Skeleton } from '@/components/ui/skeleton'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { Header } from '@/components/header'
import { HistoryTable } from '@/components/history/history-table'
import { CreateJobSheet } from '@/components/jobs/create-job-sheet'
import { JobSheet } from '@/components/jobs/job-sheet'
import { JobsTable } from '@/components/jobs/jobs-table'
import { RunningTable } from '@/components/running-table'
import { Stats, type StatKey } from '@/components/stats'
import { emptyQuery, useDashboard } from '@/hooks/use-dashboard'
import { jobKey } from '@/lib/format'
import type { HistoryFilter, Job, JobQuery } from '@/types'

type Tab = 'jobs' | 'running' | 'history'

// The preset's active tab is a white pill on a near-white track; fill it with the accent instead
const tabTrigger =
  'px-3 data-active:bg-primary data-active:text-primary-foreground dark:data-active:border-transparent dark:data-active:bg-primary dark:data-active:text-primary-foreground ' +
  'data-active:[&_[data-slot=badge]]:bg-primary-foreground/20 data-active:[&_[data-slot=badge]]:text-primary-foreground'
const tabCount = 'h-4.5 min-w-4.5 px-1 tabular-nums'

export default function App() {
  const [tab, setTab] = useState<Tab>('jobs')
  const [jobQuery, setJobQuery] = useState<JobQuery>(emptyQuery)
  const [historyFilter, setHistoryFilter] = useState<HistoryFilter>({ status: 'all', job: null })
  const [selection, setSelection] = useState<Pick<Job, 'group' | 'name'> | null>(null)
  const [creating, setCreating] = useState(false)
  const dashboard = useDashboard(jobQuery, selection)
  const { jobs, page, jobTypes, scheduler, running, summary, error, loading, refresh, actions } =
    dashboard

  // From the live page when it is there, so the sheet opens at once and refreshes with polling; otherwise
  // the dashboard loads it on its own (e.g. opened from history)
  const selectedKey = selection && jobKey(selection)
  const selected =
    (selection && jobs.find(j => jobKey(j) === selectedKey)) ??
    (dashboard.selected && jobKey(dashboard.selected) === selectedKey ? dashboard.selected : null)
  const canManage = scheduler?.canManage ?? false
  const historyEnabled = scheduler?.historyEnabled ?? false
  const showScheduleAssistant = scheduler?.showScheduleAssistant ?? false
  const secretReferences = scheduler?.secretReferences ?? false

  const changeTab = (next: Tab) => {
    setTab(next)
    if (next === 'running') void refresh()
  }

  const selectStat = (key: StatKey) => {
    if (key === 'running') return changeTab('running')
    if (key === 'failed') {
      setHistoryFilter({ status: 'Failed', job: null })
      return changeTab('history')
    }
    setJobQuery(q => ({ ...q, status: key === 'all' ? null : key, page: 1 }))
    changeTab('jobs')
  }

  const showJobHistory = (group: string, name: string) => {
    setSelection(null)
    setHistoryFilter({ status: 'all', job: { group, name } })
    changeTab('history')
  }

  // The stat card matching what is on screen, if any
  const activeStat: StatKey | null =
    tab === 'running'
      ? 'running'
      : tab === 'history'
        ? historyFilter.status === 'Failed' && !historyFilter.job
          ? 'failed'
          : null
        : jobQuery.status === null || jobQuery.status === 'Active'
          ? 'all'
          : jobQuery.status

  return (
    <div className="mx-auto max-w-7xl space-y-5 p-4 sm:p-6">
      <Header scheduler={scheduler} />

      {error && (
        <Alert variant="destructive">
          <WifiOff />
          <AlertTitle>
            {scheduler ? 'Lost connection to the scheduler' : 'Could not reach the scheduler'}
          </AlertTitle>
          <AlertDescription>
            <p>{error}</p>
            <Button variant="outline" size="sm" className="mt-2" onClick={refresh}>
              <RotateCw /> Try again
            </Button>
          </AlertDescription>
        </Alert>
      )}

      {loading ? (
        <div className="space-y-4">
          <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
            {Array.from({ length: 4 }, (_, i) => (
              <Skeleton key={i} className="h-[74px]" />
            ))}
          </div>
          <Skeleton className="h-80" />
        </div>
      ) : (
        scheduler && (
          <>
            <Stats
              total={page?.counts.all ?? 0}
              running={running.jobs.length}
              paused={page?.counts.paused ?? 0}
              unloadable={page?.counts.unloadable ?? 0}
              failed={summary?.failed ?? null}
              active={activeStat}
              onSelect={selectStat}
            />

            <Tabs value={tab} onValueChange={v => changeTab(v as Tab)}>
              <TabsList>
                <TabsTrigger value="jobs" className={tabTrigger}>
                  Jobs
                  <Badge variant="secondary" className={tabCount}>
                    {page?.counts.all ?? 0}
                  </Badge>
                </TabsTrigger>
                <TabsTrigger value="running" className={tabTrigger}>
                  Running
                  <Badge variant="secondary" className={tabCount}>
                    {running.jobs.length}
                  </Badge>
                </TabsTrigger>
                {historyEnabled && (
                  <TabsTrigger value="history" className={tabTrigger}>
                    History
                  </TabsTrigger>
                )}
              </TabsList>
              <TabsContent value="jobs" className="pt-2">
                <JobsTable
                  jobs={jobs}
                  canManage={canManage}
                  canCreate={jobTypes.length > 0}
                  page={page}
                  query={jobQuery}
                  onQueryChange={setJobQuery}
                  selectedKey={selectedKey}
                  onSelect={job => setSelection({ group: job.group, name: job.name })}
                  onNewJob={() => setCreating(true)}
                  actions={actions}
                />
              </TabsContent>
              <TabsContent value="running" className="pt-2">
                <RunningTable
                  running={running}
                  canManage={canManage}
                  localInstanceId={scheduler?.schedulerInstanceId}
                  onCancel={actions.cancel}
                />
              </TabsContent>
              {historyEnabled && (
                <TabsContent value="history" className="pt-2">
                  <HistoryTable
                    filter={historyFilter}
                    onFilterChange={setHistoryFilter}
                    onOpenJob={(group, name) => setSelection({ group, name })}
                  />
                </TabsContent>
              )}
            </Tabs>
          </>
        )
      )}

      <JobSheet
        job={selected}
        jobType={jobTypes.find(t => t.typeName === selected?.jobType)}
        canManage={canManage}
        historyEnabled={historyEnabled}
        showScheduleAssistant={showScheduleAssistant}
        secretReferences={secretReferences}
        actions={actions}
        onClose={() => setSelection(null)}
        onViewHistory={showJobHistory}
      />
      <CreateJobSheet
        open={creating}
        onOpenChange={setCreating}
        jobTypes={jobTypes}
        showScheduleAssistant={showScheduleAssistant}
        secretReferences={secretReferences}
        onCreate={actions.create}
      />
    </div>
  )
}
