import { lazy, Suspense, useEffect, useRef, useState } from 'react'
import { RotateCw, WifiOff } from 'lucide-react'
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Skeleton } from '@/components/ui/skeleton'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { AreaNav } from '@/components/area-nav'
import { Header } from '@/components/header'
import { HistoryTable } from '@/components/history/history-table'
import { CreateJobSheet } from '@/components/jobs/create-job-sheet'
import { ImportJobsSheet } from '@/components/jobs/import-jobs-sheet'
import { JobSheet } from '@/components/jobs/job-sheet'
import { JobsTable } from '@/components/jobs/jobs-table'
import { RunningTable } from '@/components/running-table'
import { Stats, StatsSkeleton, type StatKey } from '@/components/stats'
import { currentScheduler, selectScheduler } from '@/api'
import { refreshMs, useDashboard } from '@/hooks/use-dashboard'
import { useConversation } from '@/hooks/use-conversation'
import { useLayout } from '@/hooks/use-layout'
import { RefreshInterval } from '@/hooks/use-polling'
import { jobKey } from '@/lib/format'
import { cn } from '@/lib/utils'
import { defaultView, readView, showView, type Area, type Tab, type View } from '@/lib/view-state'
import type { HistoryFilter, Job, JobQuery } from '@/types'

// The preset's active tab is a white pill on a near-white track; fill it with the accent instead. Its hover rule
// would turn the active tab's text dark on the fill, and a see-through badge is too faint on it, so both stay solid.
const tabTrigger =
  'px-3 data-active:bg-primary data-active:text-primary-foreground data-active:hover:text-primary-foreground dark:data-active:border-transparent dark:data-active:bg-primary dark:data-active:text-primary-foreground ' +
  'data-active:[&_[data-slot=badge]]:bg-primary-foreground data-active:[&_[data-slot=badge]]:text-primary'
const tabCount = 'h-4.5 min-w-4.5 px-1 tabular-nums'

// AI mode and its Markdown renderer load when it is first opened, as most visits never open it
const Assistant = lazy(() =>
  import('@/components/assistant/assistant').then(m => ({ default: m.Assistant }))
)

export default function App() {
  const [scheduler, setScheduler] = useState(currentScheduler)
  // The schedulers the app serves, as the last dashboard learned them: the host's own first
  const [schedulers, setSchedulers] = useState<string[]>([])
  const changeScheduler = (name: string | null) => {
    selectScheduler(name)
    // Its view (tab, filters, open job) belonged to the last scheduler
    showView(defaultView, false)
    setScheduler(name)
  }
  // Another scheduler starts afresh, so no job, filter or open sheet carries over from the last one. Its header knows
  // its name and the others' while it loads, so the title and the switch stay put.
  return (
    <Dashboard
      key={scheduler ?? ''}
      known={{ name: scheduler ?? schedulers[0], schedulers }}
      onSchedulers={setSchedulers}
      onSchedulerChange={changeScheduler}
    />
  )
}

/** The dashboard for one scheduler. Null names the host's own. */
function Dashboard({
  known,
  onSchedulers,
  onSchedulerChange,
}: {
  /** What is known of the scheduler before it answers */
  known: { name?: string; schedulers: string[] }
  /** Told the schedulers the app serves, once this one has said */
  onSchedulers: (schedulers: string[]) => void
  onSchedulerChange: (name: string | null) => void
}) {
  // The view starts as the page's address names it, so links and reloads open where they were
  const [initial] = useState(() => readView(window.location.search))
  const [area, setArea] = useState<Area>(initial.area)
  const [tab, setTab] = useState<Tab>(initial.tab)
  const [jobQuery, setJobQuery] = useState<JobQuery>(initial.jobQuery)
  const [historyFilter, setHistoryFilter] = useState<HistoryFilter>(initial.historyFilter)
  const [selection, setSelection] = useState<Pick<Job, 'group' | 'name'> | null>(initial.selection)
  const [creating, setCreating] = useState(false)
  const [importing, setImporting] = useState(false)
  const [layout, changeLayout] = useLayout()
  // Kept here, so it is still there after a look at the jobs; each scheduler has its own
  const [turns, setTurns] = useConversation(currentScheduler() ?? '')

  // The address follows the view. Another area or tab, or a job opened, is a step Back returns from; filters,
  // paging and closing a job change the current step.
  const shown = useRef<View>(initial)
  useEffect(() => {
    const view = { area, tab, jobQuery, historyFilter, selection }
    const opened =
      selection &&
      (!shown.current.selection || jobKey(selection) !== jobKey(shown.current.selection))
    showView(view, area !== shown.current.area || tab !== shown.current.tab || Boolean(opened))
    shown.current = view
  }, [area, tab, jobQuery, historyFilter, selection])
  // Back and Forward show the view their address names
  useEffect(() => {
    const onPopState = () => {
      const view = readView(window.location.search)
      shown.current = view
      setArea(view.area)
      setTab(view.tab)
      setJobQuery(view.jobQuery)
      setHistoryFilter(view.historyFilter)
      setSelection(view.selection)
    }
    window.addEventListener('popstate', onPopState)
    return () => window.removeEventListener('popstate', onPopState)
  }, [])
  // Known from the last scheduler loaded, so a link to AI mode in an app without it keeps the dashboard refreshing
  const [hasAssistant, setHasAssistant] = useState(false)
  const dashboard = useDashboard(jobQuery, selection, area === 'assistant' && hasAssistant)
  const { jobs, page, jobTypes, scheduler, running, summary, error, loading, refresh, actions } =
    dashboard
  // Passed on when the list changes, not on every refresh, for the next scheduler's header
  const served = scheduler?.schedulers.join('\n')
  useEffect(() => {
    if (served !== undefined) onSchedulers(served.split('\n'))
  }, [served, onSchedulers])

  // From the live page when it is there, so the sheet opens at once and refreshes with polling; otherwise
  // the dashboard loads it on its own (e.g. opened from history)
  const selectedKey = selection && jobKey(selection)
  const selected =
    (selection && jobs.find(j => jobKey(j) === selectedKey)) ??
    (dashboard.selected && jobKey(dashboard.selected) === selectedKey ? dashboard.selected : null)
  const canManage = scheduler?.canManage ?? false
  const historyEnabled = scheduler?.historyEnabled ?? false
  // A link to History, where history is off, shows the jobs
  const shownTab = tab === 'history' && !historyEnabled ? 'jobs' : tab
  const secretReferences = scheduler?.secretReferences ?? false
  // Areas the app turns on; a link to one it does not have shows the dashboard. An API without features (the
  // website's simulated one, until it adds them) has none
  const assistant = scheduler?.features?.includes('assistant') ?? false
  if (assistant !== hasAssistant) setHasAssistant(assistant)
  const areas: Area[] = ['dashboard', ...(assistant ? (['assistant'] as const) : [])]
  const shownArea = areas.includes(area) ? area : 'dashboard'
  // Kept once opened, hidden while the dashboard is shown, so an answer being written carries on meanwhile
  const [assistantOpened, setAssistantOpened] = useState(false)
  if (shownArea === 'assistant' && !assistantOpened) setAssistantOpened(true)

  const changeTab = (next: Tab) => {
    setArea('dashboard')
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

  // The stat card matching what is on screen, if any. The row stays on every tab, so switching tabs never moves the tab
  // bar out from under the pointer.
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
    <RefreshInterval.Provider value={refreshMs(scheduler)}>
      <div
        className={cn(
          'mx-auto space-y-5 p-4 sm:p-6',
          layout.fullWidth ? 'max-w-none' : 'max-w-7xl',
          layout.compact &&
            '[&_[data-slot=table-cell]]:py-1 [&_[data-slot=table-head]]:h-8 [&_[data-slot=table-cell]]:text-[13px]'
        )}
        data-density={layout.compact ? 'compact' : undefined}
      >
        <Header
          scheduler={scheduler}
          known={known}
          layout={layout}
          onLayoutChange={changeLayout}
          // The host's own scheduler, first in the list, needs no ?scheduler= in the URL
          onSchedulerChange={name =>
            onSchedulerChange(name === (scheduler?.schedulers ?? known.schedulers)[0] ? null : name)
          }
          nav={
            areas.length > 1 && <AreaNav areas={areas} area={shownArea} onAreaChange={setArea} />
          }
        />

        {error && (
          <Alert variant="destructive">
            <WifiOff />
            <AlertTitle>
              {scheduler ? 'Lost connection to the scheduler' : 'Could not reach the scheduler'}
            </AlertTitle>
            <AlertDescription>
              <p>{error}</p>
              <div className="mt-2 flex flex-wrap gap-2">
                <Button variant="outline" size="sm" onClick={refresh}>
                  <RotateCw /> Try again
                </Button>
                {/* e.g. a scheduler named in the URL that has since been shut down */}
                {currentScheduler() && (
                  <Button variant="outline" size="sm" onClick={() => onSchedulerChange(null)}>
                    Show the main scheduler
                  </Button>
                )}
              </div>
            </AlertDescription>
          </Alert>
        )}

        {loading ? (
          <div className="space-y-4">
            <StatsSkeleton />
            <Skeleton className="h-80" />
          </div>
        ) : (
          scheduler && (
            <main className="space-y-5">
              {assistant && assistantOpened && (
                <div hidden={shownArea !== 'assistant'}>
                  <Suspense fallback={<Skeleton className="mx-auto h-40 max-w-3xl" />}>
                    <Assistant
                      scheduler={scheduler.schedulerName}
                      status={{
                        failed: historyEnabled ? summary?.failed : null,
                        running: running.jobs.length,
                        paused: page?.counts.paused ?? 0,
                      }}
                      turns={turns}
                      onTurnsChange={setTurns}
                      onOpenJob={(group, name) => setSelection({ group, name })}
                    />
                  </Suspense>
                </div>
              )}
              {shownArea === 'dashboard' && (
                <>
                  <Stats
                    total={page?.counts.all ?? 0}
                    running={running.jobs.length}
                    paused={page?.counts.paused ?? 0}
                    unloadable={page?.counts.unloadable ?? 0}
                    error={page?.counts.error ?? 0}
                    failed={summary?.failed ?? null}
                    active={activeStat}
                    onSelect={selectStat}
                  />

                  <Tabs value={shownTab} onValueChange={v => changeTab(v as Tab)}>
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
                        onImport={() => setImporting(true)}
                        actions={actions}
                        history={historyEnabled}
                      />
                    </TabsContent>
                    <TabsContent value="running" className="pt-2">
                      <RunningTable
                        running={running}
                        canManage={canManage}
                        localInstanceId={scheduler?.schedulerInstanceId}
                        onCancel={actions.cancel}
                        logs={historyEnabled}
                        clustered={scheduler?.jobStoreClustered ?? true}
                      />
                    </TabsContent>
                    {historyEnabled && (
                      <TabsContent value="history" className="pt-2">
                        <HistoryTable
                          filter={historyFilter}
                          onFilterChange={setHistoryFilter}
                          onOpenJob={(group, name) => setSelection({ group, name })}
                          canManage={canManage}
                          onRetry={actions.retry}
                          jobTypes={jobTypes}
                          localInstanceId={scheduler?.schedulerInstanceId}
                          clustered={scheduler?.jobStoreClustered ?? true}
                        />
                      </TabsContent>
                    )}
                  </Tabs>
                </>
              )}
            </main>
          )
        )}

        <JobSheet
          job={selected}
          jobType={jobTypes.find(t => t.typeName === selected?.jobType)}
          canManage={canManage}
          historyEnabled={historyEnabled}
          secretReferences={secretReferences}
          actions={actions}
          onClose={() => setSelection(null)}
          onViewHistory={showJobHistory}
        />
        <CreateJobSheet
          open={creating}
          onOpenChange={setCreating}
          jobTypes={jobTypes}
          secretReferences={secretReferences}
          onCreate={actions.create}
        />
        <ImportJobsSheet
          open={importing}
          onOpenChange={setImporting}
          onImported={() => void refresh()}
        />
      </div>
    </RefreshInterval.Provider>
  )
}
