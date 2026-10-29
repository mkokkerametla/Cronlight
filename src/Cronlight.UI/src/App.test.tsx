import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { api, askAssistant, currentScheduler, selectScheduler } from '@/api'
import App from '@/App'
import { TooltipProvider } from '@/components/ui/tooltip'
import { execution, job, jobPage, scheduler, trigger } from '@/test/fixtures'
import type { AssistantEvent, HistorySummary, Job, JobType } from '@/types'

vi.mock('@/api', async importOriginal => ({
  ...(await importOriginal<typeof import('@/api')>()),
  api: vi.fn(),
  askAssistant: vi.fn(),
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
  screen.getByText(name, { selector: 'button' }).closest('tr') as HTMLElement

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
    // The dashboard keeps its view in the address, which the next test must not inherit
    window.history.replaceState(null, '', '/')
    localStorage.removeItem('cronlight-layout')
  })

  it('uses the full width unless narrowed, and compact rows when asked, and remembers both in this browser', async () => {
    const { unmount } = renderApp()
    await screen.findByRole('tabpanel')
    const page = () => screen.getByRole('tablist').closest('div.mx-auto') as HTMLElement
    expect(page()).toHaveClass('max-w-none')

    await userEvent.click(screen.getByRole('button', { name: 'Change layout' }))
    await userEvent.click(screen.getByRole('menuitemcheckbox', { name: 'Full width' }))
    expect(page()).toHaveClass('max-w-7xl')
    await userEvent.click(screen.getByRole('button', { name: 'Change layout' }))
    await userEvent.click(screen.getByRole('menuitemcheckbox', { name: 'Compact rows' }))
    expect(page()).toHaveAttribute('data-density', 'compact')

    unmount()
    renderApp()
    await screen.findByRole('tabpanel')
    expect(page()).toHaveClass('max-w-7xl')
    expect(page()).toHaveAttribute('data-density', 'compact')
  })

  it('uses the standard layout when the one remembered cannot be read', async () => {
    localStorage.setItem('cronlight-layout', '{not json')
    renderApp()
    await screen.findByRole('tabpanel')

    const page = screen.getByRole('tablist').closest('div.mx-auto') as HTMLElement
    expect(page).toHaveClass('max-w-none')
    expect(page).not.toHaveAttribute('data-density')
  })

  it('opens the view a link names: tab, filter and job', async () => {
    window.history.replaceState(null, '', '/?tab=history&outcome=Failed')
    const { unmount } = renderApp()
    await waitFor(() => expect(tab('History')).toHaveAttribute('aria-selected', 'true'))
    await waitFor(() =>
      expect(requested('history?')).toContainEqual(expect.stringContaining('status=Failed'))
    )
    unmount()

    window.history.replaceState(null, '', '/?group=billing&job=invoices')
    renderApp()
    expect(await screen.findByRole('dialog', { name: 'invoices' })).toBeInTheDocument()
  })

  it('shows the jobs for a link to History when history is off', async () => {
    responses.scheduler = scheduler({ historyEnabled: false })
    window.history.replaceState(null, '', '/?tab=history')
    renderApp()

    expect(await screen.findByText('invoices', { selector: 'button' })).toBeInTheDocument()
    expect(tab('Jobs')).toHaveAttribute('aria-selected', 'true')
  })

  it('keeps the view in the address, so Back returns to the one before', async () => {
    renderApp()
    await screen.findByRole('tabpanel')

    await userEvent.click(tab('History'))
    expect(new URLSearchParams(window.location.search).get('tab')).toBe('history')

    // The browser's Back button, back to the jobs
    window.history.replaceState(null, '', '/')
    act(() => window.dispatchEvent(new PopStateEvent('popstate')))
    await waitFor(() => expect(tab('Jobs')).toHaveAttribute('aria-selected', 'true'))
  })

  it("starts another scheduler's dashboard without the view of the last one", async () => {
    const schedulers = ['SampleHost', 'Reports']
    responses.scheduler = scheduler({ schedulers })
    window.history.replaceState(null, '', '/?tab=history')
    renderApp()
    await waitFor(() => expect(tab('History')).toHaveAttribute('aria-selected', 'true'))

    await userEvent.click(screen.getByRole('combobox', { name: 'Scheduler' }))
    await userEvent.click(screen.getByRole('option', { name: 'Reports' }))

    expect(window.location.search).toBe('?scheduler=Reports')
    await waitFor(() => expect(tab('Jobs')).toHaveAttribute('aria-selected', 'true'))
  })

  it('switches to another scheduler, starting afresh, and back to the host’s own', async () => {
    const schedulers = ['SampleHost', 'Reports']
    responses.scheduler = scheduler({ schedulers })
    // The server answers for the scheduler a request names, as the real one does with ?scheduler=
    const hostServer = vi.mocked(api).getMockImplementation()!
    // Each scheduler offers its own job types, so they are asked for again
    const jobTypesFor: (string | null)[] = []
    vi.mocked(api).mockImplementation(async (path, options) => {
      if (path === 'job-types') jobTypesFor.push(currentScheduler())
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
    await waitFor(() => expect(jobTypesFor).toEqual([null, 'Reports', null]))
  })

  it('keeps the name of the scheduler switched to, and the switch, in the header while it loads', async () => {
    const schedulers = ['SampleHost', 'Reports']
    responses.scheduler = scheduler({ schedulers })
    const hostServer = vi.mocked(api).getMockImplementation()!
    let answer!: () => void
    const answered = new Promise<void>(resolve => (answer = resolve))
    vi.mocked(api).mockImplementation(async (path, options) => {
      if (currentScheduler() === 'Reports' && path === 'scheduler') {
        await answered
        return scheduler({ schedulerName: 'Reports', schedulers })
      }
      return hostServer(path, options)
    })
    renderApp()
    await screen.findByRole('heading', { name: 'SampleHost' })

    await userEvent.click(screen.getByRole('combobox', { name: 'Scheduler' }))
    await userEvent.click(screen.getByRole('option', { name: 'Reports' }))

    // Before Reports answers: its own name, never a stand-in, and the switch where it was
    expect(screen.getByRole('heading', { name: 'Reports' })).toBeInTheDocument()
    expect(screen.queryByText('Quartz')).toBeNull()
    expect(screen.getByRole('combobox', { name: 'Scheduler' })).toHaveTextContent('Reports')
    act(answer)
    expect(await screen.findByRole('tabpanel')).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Reports' })).toBeInTheDocument()
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

  it('keeps the stat row in place as the dashboard loads, with history on', async () => {
    let answer!: () => void
    const answered = new Promise<void>(resolve => (answer = resolve))
    const respond = vi.mocked(api).getMockImplementation()!
    vi.mocked(api).mockImplementation(async (path, options) => {
      if (path === 'scheduler') await answered
      return respond(path, options)
    })
    const { container } = renderApp()
    // The row of card placeholders, apart from the title's
    const placeholders = [...container.querySelectorAll('[data-slot="skeleton"]')]
      .map(skeleton => skeleton.parentElement!)
      .find(row => [...row.children].every(c => c.getAttribute('data-slot') === 'skeleton'))!
    const loading = { columns: placeholders.className, cards: placeholders.childElementCount }

    act(answer)

    const row = (await screen.findByRole('button', { name: /Total jobs/ })).parentElement!
    expect({ columns: row.className, cards: row.childElementCount }).toEqual(loading)
  })

  it('filters the jobs from the Paused card, on the server', async () => {
    renderApp()
    await userEvent.click(await screen.findByRole('button', { name: /Paused/ }))

    expect(card('Paused')).toHaveAttribute('aria-pressed', 'true')
    await waitFor(() => expect(screen.queryByText('invoices', { selector: 'button' })).toBeNull())
    expect(jobRow('cleanup')).toBeInTheDocument()
    expect(requested('jobs/search?').at(-1)).toContain('status=Paused')
    // The cards count every job, not just the filtered page
    expect(card('Total jobs')).toHaveTextContent('2')

    // Total jobs lists them all again
    await userEvent.click(card('Total jobs'))
    expect(await screen.findByText('invoices', { selector: 'button' })).toBeInTheDocument()
    expect(requested('jobs/search?').at(-1)).not.toContain('status=')
    expect(card('Total jobs')).toHaveAttribute('aria-pressed', 'true')
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
    expect(screen.getByRole('combobox', { name: 'Filter by outcome' })).toHaveTextContent('Failed')
    await waitFor(() => expect(requested('history?').at(-1)).toContain('status=Failed'))
  })

  it('keeps the stat row on History, so the tabs stay where they were clicked', async () => {
    renderApp()
    await userEvent.click(await screen.findByRole('tab', { name: /History/ }))

    expect(card('Total jobs')).toBeInTheDocument()
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
    // Already the host's own: there is no other to fall back to
    expect(screen.queryByRole('button', { name: 'Show the main scheduler' })).toBeNull()

    responses.scheduler = scheduler()
    await userEvent.click(screen.getByRole('button', { name: /Try again/ }))

    expect(await screen.findByRole('heading', { name: 'SampleHost' })).toBeInTheDocument()
    expect(screen.queryByText('Could not reach the scheduler')).toBeNull()
  })

  it('offers the main scheduler when the one a link names cannot be reached', async () => {
    const hostServer = vi.mocked(api).getMockImplementation()!
    vi.mocked(api).mockImplementation(async (path, options) => {
      if (currentScheduler() === 'Retired' && path === 'scheduler')
        throw new Error('No scheduler named Retired')
      return hostServer(path, options)
    })
    selectScheduler('Retired')
    renderApp()

    expect(await screen.findByText('No scheduler named Retired')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Show the main scheduler' }))

    expect(currentScheduler()).toBeNull()
    expect(window.location.search).not.toContain('scheduler=')
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

  it('offers AI mode only when the app adds the assistant, and keeps it in the address', async () => {
    const { unmount } = renderApp()
    await screen.findByRole('tabpanel')
    expect(screen.queryByRole('navigation', { name: 'Areas' })).toBeNull()
    unmount()

    responses.scheduler = scheduler({ features: ['assistant'] })
    renderApp()
    const nav = await screen.findByRole('navigation', { name: 'Areas' })
    await userEvent.click(within(nav).getByRole('button', { name: 'AI mode' }))

    // AI mode loads when first opened
    expect(await screen.findByRole('textbox', { name: 'Your question' })).toBeInTheDocument()
    expect(screen.queryByRole('tablist')).toBeNull()
    expect(window.location.search).toBe('?area=assistant')
    expect(within(nav).getByRole('button', { name: 'AI mode' })).toHaveAttribute(
      'aria-current',
      'page'
    )

    // Back returns to the jobs
    await act(async () => {
      window.history.back()
      await new Promise(resolve => window.addEventListener('popstate', resolve, { once: true }))
    })
    expect(await screen.findByRole('tablist')).toBeInTheDocument()
  })

  it('keeps writing an answer while the jobs are looked at, and checks only the scheduler meanwhile', async () => {
    let send!: (event: AssistantEvent) => void
    vi.mocked(askAssistant).mockImplementation(
      (_messages, onEvent) =>
        new Promise<void>(() => {
          send = onEvent
        })
    )
    responses.scheduler = scheduler({ features: ['assistant'] })
    window.history.replaceState(null, '', '/?area=assistant')
    renderApp()
    await userEvent.type(
      await screen.findByRole('textbox', { name: 'Your question' }),
      'What failed?{Enter}'
    )
    const nav = screen.getByRole('navigation', { name: 'Areas' })

    await userEvent.click(within(nav).getByRole('button', { name: 'Dashboard' }))
    act(() => send({ type: 'text', text: 'Two jobs failed.' }))
    await userEvent.click(within(nav).getByRole('button', { name: 'AI mode' }))

    expect(screen.getByText('Two jobs failed.')).toBeInTheDocument()
    expect(screen.queryByText('Stopped.')).toBeNull()
    expect(screen.getByRole('button', { name: 'Stop' })).toBeInTheDocument()
  })

  it('shows the dashboard for a link to AI mode when the app has no assistant', async () => {
    window.history.replaceState(null, '', '/?area=assistant')
    renderApp()

    expect(await screen.findByRole('tablist')).toBeInTheDocument()
    expect(screen.queryByRole('textbox', { name: 'Your question' })).toBeNull()
  })
})
