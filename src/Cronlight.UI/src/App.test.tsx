import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { api } from '@/api'
import App from '@/App'
import { TooltipProvider } from '@/components/ui/tooltip'
import { execution, job, scheduler, trigger } from '@/test/fixtures'
import type { HistorySummary, JobType } from '@/types'

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
      const response = responses[path]
      if (response instanceof Error) throw response
      return response
    })
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

  it('filters the jobs from the Paused card', async () => {
    renderApp()
    await userEvent.click(await screen.findByRole('button', { name: /Paused/ }))

    expect(card('Paused')).toHaveAttribute('aria-pressed', 'true')
    expect(screen.queryByText('invoices', { selector: 'div' })).toBeNull()
    expect(jobRow('cleanup')).toBeInTheDocument()
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
