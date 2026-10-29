import type { ExecutionStatus, HistoryFilter, JobQuery, JobStatus } from '@/types'

export type Tab = 'jobs' | 'running' | 'history'

/** The dashboard's areas: the jobs, with their tabs, and the assistant when the app adds it */
export type Area = 'dashboard' | 'assistant'

/** What the dashboard shows. The page's address keeps it, so a link or the Back button opens the same view. */
export interface View {
  area: Area
  tab: Tab
  jobQuery: JobQuery
  historyFilter: HistoryFilter
  /** The job whose panel is open */
  selection: { group: string; name: string } | null
}

export const defaultView: View = {
  area: 'dashboard',
  tab: 'jobs',
  jobQuery: { search: '', type: null, status: null, page: 1 },
  historyFilter: { status: 'all', job: null },
  selection: null,
}

const areas: readonly Area[] = ['dashboard', 'assistant']
const tabs: readonly Tab[] = ['jobs', 'running', 'history']
const jobStatuses: readonly JobStatus[] = ['Active', 'Paused', 'Unloadable', 'Error']
const outcomes: readonly ExecutionStatus[] = [
  'Running',
  'Succeeded',
  'Failed',
  'Vetoed',
  'Cancelled',
  'Retried',
  'Misfired',
]

// The parameters a view owns; the address's others, such as ?scheduler=, are left as they are
const viewParams = [
  'area',
  'tab',
  'q',
  'type',
  'status',
  'page',
  'outcome',
  'runsGroup',
  'runsJob',
  'group',
  'job',
]

const pick = <T extends string>(value: string | null, allowed: readonly T[]) =>
  allowed.find(a => a === value) ?? null

/** The view a query string names. Anything missing or unknown is the default's. */
export function readView(search: string): View {
  const params = new URLSearchParams(search)
  const page = Number(params.get('page'))
  const [group, job] = [params.get('group'), params.get('job')]
  const [runsGroup, runsJob] = [params.get('runsGroup'), params.get('runsJob')]
  return {
    area: pick(params.get('area'), areas) ?? defaultView.area,
    tab: pick(params.get('tab'), tabs) ?? defaultView.tab,
    jobQuery: {
      search: params.get('q') ?? '',
      type: params.get('type') || null,
      status: pick(params.get('status'), jobStatuses),
      page: Number.isInteger(page) && page > 1 ? page : 1,
    },
    historyFilter: {
      status: pick(params.get('outcome'), outcomes) ?? 'all',
      job: runsGroup && runsJob ? { group: runsGroup, name: runsJob } : null,
    },
    selection: group && job ? { group, name: job } : null,
  }
}

/** The query string for a view: what differs from the default view, after the parameters a view does not own */
export function viewSearch(view: View, search: string): string {
  const params = new URLSearchParams(search)
  for (const name of viewParams) params.delete(name)
  const set = (name: string, value: string | null) => {
    if (value) params.set(name, value)
  }
  set('area', view.area === defaultView.area ? null : view.area)
  set('tab', view.tab === defaultView.tab ? null : view.tab)
  set('q', view.jobQuery.search)
  set('type', view.jobQuery.type)
  set('status', view.jobQuery.status)
  set('page', view.jobQuery.page > 1 ? String(view.jobQuery.page) : null)
  set('outcome', view.historyFilter.status === 'all' ? null : view.historyFilter.status)
  set('runsGroup', view.historyFilter.job?.group ?? null)
  set('runsJob', view.historyFilter.job?.name ?? null)
  set('group', view.selection?.group ?? null)
  set('job', view.selection?.name ?? null)
  const text = params.toString()
  return text ? `?${text}` : ''
}

/** Shows a view in the page's address: as a new step Back returns from, or in place of the current one */
export function showView(view: View, step: boolean) {
  const search = viewSearch(view, window.location.search)
  if (search === window.location.search) return
  const url = `${window.location.pathname}${search}${window.location.hash}`
  if (step) window.history.pushState(null, '', url)
  else window.history.replaceState(window.history.state, '', url)
}
