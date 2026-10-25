import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { api, currentScheduler, selectScheduler } from '@/api'
import App from '@/App'
import { TooltipProvider } from '@/components/ui/tooltip'
import { execution, job, jobPage, scheduler, trigger } from '@/test/fixtures'
import type { HistorySummary, Job, JobType } from '@/types'

vi.mock('@/api', async importOriginal => ({
  ...(await importOriginal<typeof import('@/api')>()),
  api: vi.fn(),
}))

const summary: HistorySummary = {
  sinceUtc: '2030-01-01T00:00:00Z',
  succeeded: 5,
  failed: 2,
  vetoed: 0,
  running: 0,
}
const jobTypes: JobType[] = [
  {
    typeName: 'Acme.Jobs.NightlyJob',
    displayName: 'Nightly',
    description: null,
    hidden: false,
    parameters: [],
    retry: null,
  },
]

/** The server, answering by path; a test changes an answer or makes it an Error. */
let responses: Record<string, unknown>
const requested = (prefix: string) =>
  vi
    .mocked(api)
    .mock.calls.map(([path]) => path)
    .filter(path => path.startsWith(prefix))

const renderApp = () =>
  render(
    <TooltipProvider>
      <App />
    </TooltipProvider>
  )

const tab = (name: string) => screen.getByRole('tab', { name: new RegExp(name) })
const card = (label: string) => screen.getByRole('button', { name: new RegExp(label) })
const jobRow = (name: string) =>
  screen.getByText(name, { selector: 'div' }).closest('tr') as HTMLElement

describe('App', () => {
  beforeEach(() => {
    responses = {
      jobs: [
        job({ name: 'invoices', group: 'billing' }),
        job({ name: 'cleanup', triggers: [trigger({ state: 'Paused' })] }),
      ],
      scheduler: scheduler(),
      running: { source: 'local', jobs: [] },
      'history/summary?hours=24': summary,
      'job-types': jobTypes,
    }
    vi.mocked(api).mockImplementation(async (path, options) => {
      if (options?.method) return undefined
      if (path.startsWith('history?'))
        return {
          records: [execution(1, { jobGroup: 'billing', jobName: 'invoices', status: 'Failed' })],
          nextBefore: null,
        }
      const response = responses[path.startsWith('jobs/search?') ? 'jobs' : path]
      if (response instanceof Error) throw response
      return path.startsWith('jobs/search?') ? jobPage(response as Job[], path) : response
    })
  })

  afterEach(() => {
    selectScheduler(null)
  })

  it('switches to another scheduler, starting afresh, and back to the host’s own', async () => {
    const schedulers = ['SampleHost', 'Reports']
    responses.scheduler = scheduler({ schedulers })
    // The server answers for the scheduler a request names, as the real one does with ?scheduler=
    const hostServer = vi.mocked(api).getMockImplementation()!
    vi.mocked(api).mockImplementation(async (path, options) => {
      if (currentScheduler() !== 'Reports') return hostServer(path, options)
      if (path === 'scheduler') return scheduler({ schedulerName: 'Reports', schedulers })
      if (path.startsWith('jobs/search?')) return jobPage([job({ name: 'monthly' })], path)
      return hostServer(path, options)
    })
    renderApp()
    await userEvent.click(within(await screen.findByRole('tabpanel')).getByText('invoices'))
    await userEvent.keyboard('{Escape}')

    await userEvent.click(screen.getByRole('combobox', { name: 'Scheduler' }))
    await userEvent.click(screen.getByRole('option', { name: 'Reports' }))

    expect(currentScheduler()).toBe('Reports')
    expect(within(await screen.findByRole('tabpanel')).getByText('monthly')).toBeInTheDocument()
    expect(screen.queryByText('invoices')).toBeNull()
    expect(screen.getByRole('heading', { name: 'Reports' })).toBeInTheDocument()

    await userEvent.click(screen.getByRole('combobox', { name: 'Scheduler' }))
    await userEvent.click(screen.getByRole('option', { name: 'SampleHost' }))
    expect(currentScheduler()).toBeNull()
  })

  it('shows the scheduler, its counts and its jobs once loaded', async () => {
    renderApp()

    expect(await screen.findByRole('heading', { name: 'SampleHost' })).toBeInTheDocument()
    expect(card('Total jobs')).toHaveTextContent('2')
    expect(card('Paused')).toHaveTextContent('1')
    expect(card('Failed')).toHaveTextContent('2')
    expect(tab('Jobs')).toHaveTextContent('2')
    expect(jobRow('invoices')).toBeInTheDocument()
    expect(card('Total jobs')).toHaveAttribute('aria-pressed', 'true')
  })

  it('filters the jobs from the Paused card, on the server', async () => {
    renderApp()
    await userEvent.click(await screen.findByRole('button', { name: /Paused/ }))

    expect(card('Paused')).toHaveAttribute('aria-pressed', 'true')
    await waitFor(() => expect(screen.queryByText('invoices', { selector: 'div' })).toBeNull())
    expect(jobRow('cleanup')).toBeInTheDocument()
    expect(requested('jobs/search?').at(-1)).toContain('status=Paused')
    // The cards count every job, not just the filtered page
    expect(card('Total jobs')).toHaveTextContent('2')
  })

  it('opens a job from history even when it is not on the current page', async () => {
    responses['jobs/DEFAULT/elsewhere'] = job({ name: 'elsewhere' })
    vi.mocked(api).mockImplementation(async (path, options) => {
      if (options?.method) return undefined
      if (path.startsWith('history?'))
        return { records: [execution(1, { jobName: 'elsewhere' })], nextBefore: null }
      if (path.startsWith('jobs/search?')) return jobPage(responses.jobs as Job[], path)
      return responses[path]
    })
    renderApp()
    await userEvent.click(await screen.findByRole('tab', { name: /History/ }))

    await userEvent.click(await screen.findByRole('button', { name: 'elsewhere' }))

    expect(await screen.findByRole('dialog', { name: 'elsewhere' })).toBeInTheDocument()
    expect(requested('jobs/DEFAULT/elsewhere')).not.toHaveLength(0)
  })

  it('opens the running tab from its card and loads it straight away', async () => {
    renderApp()
    await screen.findByRole('heading', { name: 'SampleHost' })
    const before = requested('running').length

    await userEvent.click(card('Running now'))

    expect(tab('Running')).toHaveAttribute('aria-selected', 'true')
    expect(screen.getByText('No jobs running')).toBeInTheDocument()
    expect(requested('running').length).toBe(before + 1)
  })

  it('opens the history of failed runs from the Failed card', async () => {
    renderApp()
    await userEvent.click(await screen.findByRole('button', { name: /Failed/ }))

    expect(tab('History')).toHaveAttribute('aria-selected', 'true')
    expect(card('Failed')).toHaveAttribute('aria-pressed', 'true')
    await waitFor(() => expect(requested('history?').at(-1)).toContain('status=Failed'))
  })

  it('goes from a job to its runs, and from a run back to its job', async () => {
    renderApp()
    await userEvent.click(within(await screen.findByRole('tabpanel')).getByText('invoices'))
    const sheet = within(screen.getByRole('dialog', { name: 'invoices' }))

    await userEvent.click(sheet.getByRole('button', { name: /All runs/ }))

    expect(screen.queryByRole('dialog')).toBeNull()
    expect(tab('History')).toHaveAttribute('aria-selected', 'true')
    expect(screen.getByText('billing.invoices')).toBeInTheDocument()
    expect(card('Failed')).toHaveAttribute('aria-pressed', 'false')

    await userEvent.click(await screen.findByRole('button', { name: 'invoices' }))
    expect(screen.getByRole('dialog', { name: 'invoices' })).toBeInTheDocument()
  })

  it('creates a job from the New job sheet', async () => {
    renderApp()
    await userEvent.click(await screen.findByRole('button', { name: /New job/ }))
    const sheet = within(screen.getByRole('dialog', { name: 'New job' }))
    await userEvent.type(sheet.getByLabelText('Name'), 'weekly')

    await userEvent.click(sheet.getByRole('button', { name: 'Create job' }))

    expect(api).toHaveBeenCalledWith('jobs', {
      method: 'POST',
      body: expect.objectContaining({ type: 'Acme.Jobs.NightlyJob', name: 'weekly' }),
    })
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
  })

  it('hides history when it is off, and disables New job without job types', async () => {
    responses.scheduler = scheduler({ historyEnabled: false })
    responses['job-types'] = []
    renderApp()

    await screen.findByRole('heading', { name: 'SampleHost' })
    expect(screen.queryByRole('tab', { name: /History/ })).toBeNull()
    expect(screen.queryByText('Failed (24h)')).toBeNull()
    expect(screen.getByRole('button', { name: /New job/ })).toBeDisabled()
  })

  it('explains an unreachable scheduler and retries', async () => {
    responses.scheduler = new Error('Failed to fetch')
    renderApp()

    expect(await screen.findByText('Could not reach the scheduler')).toBeInTheDocument()
    expect(screen.getByText('Failed to fetch')).toBeInTheDocument()

    responses.scheduler = scheduler()
    await userEvent.click(screen.getByRole('button', { name: /Try again/ }))

    expect(await screen.findByRole('heading', { name: 'SampleHost' })).toBeInTheDocument()
    expect(screen.queryByText('Could not reach the scheduler')).toBeNull()
  })

  it('keeps showing the last data when the connection drops', async () => {
    renderApp()
    await screen.findByRole('heading', { name: 'SampleHost' })

    responses.jobs = new Error('Failed to fetch')
    await userEvent.click(card('Running now'))

    expect(await screen.findByText('Lost connection to the scheduler')).toBeInTheDocument()
    expect(tab('Jobs')).toHaveTextContent('2')
  })
})
