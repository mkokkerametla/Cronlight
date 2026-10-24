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
import { useDashboard } from '@/hooks/use-dashboard'
import { isPaused, jobKey, type JobStatus } from '@/lib/format'
import type { HistoryFilter } from '@/types'

type Tab = 'jobs' | 'running' | 'history'

// The preset's active tab is a white pill on a near-white track; fill it with the accent instead
const tabTrigger =
  'px-3 data-active:bg-primary data-active:text-primary-foreground dark:data-active:border-transparent dark:data-active:bg-primary dark:data-active:text-primary-foreground ' +
  'data-active:[&_[data-slot=badge]]:bg-primary-foreground/20 data-active:[&_[data-slot=badge]]:text-primary-foreground'
const tabCount = 'h-4.5 min-w-4.5 px-1 tabular-nums'

export default function App() {
  const { jobs, jobTypes, scheduler, running, summary, error, loading, refresh, actions } =
    useDashboard()
  const [tab, setTab] = useState<Tab>('jobs')
  const [statusFilter, setStatusFilter] = useState<JobStatus | 'all'>('all')
  const [historyFilter, setHistoryFilter] = useState<HistoryFilter>({ status: 'all', job: null })
  const [selectedKey, setSelectedKey] = useState<string | null>(null)
  const [creating, setCreating] = useState(false)

  // Resolve the selection against the live list so the sheet refreshes with polling
  const selected = jobs.find(j => jobKey(j) === selectedKey) ?? null
  const canManage = scheduler?.canManage ?? false
  const historyEnabled = scheduler?.historyEnabled ?? false

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
    setStatusFilter(key === 'all' ? 'all' : key)
    changeTab('jobs')
  }

  const showJobHistory = (group: string, name: string) => {
    setSelectedKey(null)
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
        : statusFilter === 'all' || statusFilter === 'Active'
          ? 'all'
          : statusFilter

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
              total={jobs.length}
              running={running.jobs.length}
              paused={jobs.filter(isPaused).length}
              unloadable={jobs.filter(j => !j.loadable).length}
              failed={summary?.failed ?? null}
              active={activeStat}
              onSelect={selectStat}
            />

            <Tabs value={tab} onValueChange={v => changeTab(v as Tab)}>
              <TabsList>
                <TabsTrigger value="jobs" className={tabTrigger}>
                  Jobs
                  <Badge variant="secondary" className={tabCount}>
                    {jobs.length}
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
                  statusFilter={statusFilter}
                  onStatusFilterChange={setStatusFilter}
                  selectedKey={selectedKey}
                  onSelect={job => setSelectedKey(jobKey(job))}
                  onNewJob={() => setCreating(true)}
                  actions={actions}
                />
              </TabsContent>
              <TabsContent value="running" className="pt-2">
                <RunningTable running={running} />
              </TabsContent>
              {historyEnabled && (
                <TabsContent value="history" className="pt-2">
                  <HistoryTable
                    filter={historyFilter}
                    onFilterChange={setHistoryFilter}
                    onOpenJob={(group, name) => setSelectedKey(`${group}.${name}`)}
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
        actions={actions}
        onClose={() => setSelectedKey(null)}
        onViewHistory={showJobHistory}
      />
      <CreateJobSheet
        open={creating}
        onOpenChange={setCreating}
        jobTypes={jobTypes}
        onCreate={actions.create}
      />
    </div>
  )
}
