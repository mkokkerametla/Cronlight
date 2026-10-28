import { act, render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useState } from 'react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { api, ApiError } from '@/api'
import { HistoryTable } from '@/components/history/history-table'
import { execution, historyStats } from '@/test/fixtures'
import type { ExecutionRecord, HistoryFilter, HistoryPage, JobType } from '@/types'

vi.mock('@/api', async importOriginal => ({
  ...(await importOriginal<typeof import('@/api')>()),
  api: vi.fn(),
}))

let pages: Record<string, HistoryPage>
const onOpenJob = vi.fn()

/** The table with real filter state, as App uses it. */
function Harness({
  initial,
  onRetry,
  jobTypes,
}: {
  initial: HistoryFilter
  onRetry?: (record: ExecutionRecord) => Promise<boolean>
  jobTypes?: JobType[]
}) {
  // The fixtures' runs are on node-1, the node serving the dashboard
  const [filter, setFilter] = useState(initial)
  return (
    <HistoryTable
      filter={filter}
      onFilterChange={setFilter}
      onOpenJob={onOpenJob}
      canManage={onRetry !== undefined}
      onRetry={onRetry}
      jobTypes={jobTypes}
      localInstanceId="node-1"
    />
  )
}

const all: HistoryFilter = { status: 'all', job: null }
const calls = (prefix: string) =>
  vi
    .mocked(api)
    .mock.calls.map(c => c[0] as string)
    .filter(path => path.startsWith(prefix))
/** The query of the nth request for the list of runs */
const query = (call: number) =>
  Object.fromEntries(new URLSearchParams(calls('history?')[call].split('?')[1]))

describe('HistoryTable', () => {
  beforeEach(() => {
    pages = {}
    vi.mocked(api).mockImplementation(async path => {
      if ((path as string).startsWith('history/stats')) return historyStats()
      const params = new URLSearchParams((path as string).split('?')[1])
      return (
        pages[params.get('before') ?? params.get('status') ?? 'all'] ?? {
          records: [],
          nextBefore: null,
        }
      )
    })
  })
  afterEach(() => {
    vi.useRealTimers()
  })

  it("names each run's job type as the job list does", async () => {
    pages.all = {
      records: [
        execution(2, { jobType: 'Acme.Jobs.InvoiceJob' }),
        execution(1, { jobName: 'other', jobType: 'Acme.Jobs.UnlistedJob' }),
      ],
      nextBefore: null,
    }
    const jobTypes = [
      { typeName: 'Acme.Jobs.InvoiceJob', displayName: 'Send invoices' },
    ] as JobType[]
    render(<Harness initial={all} jobTypes={jobTypes} />)

    expect(await screen.findByText(/DEFAULT · Send invoices/)).toBeInTheDocument()
    // A type the dashboard does not describe keeps its class name
    expect(screen.getByText(/DEFAULT · UnlistedJob/)).toBeInTheDocument()
  })

  it('lists runs with outcome, error, refires and node', async () => {
    pages.all = {
      records: [
        execution(2, {
          jobName: 'invoices',
          jobGroup: 'billing',
          jobType: 'Acme.Jobs.InvoiceJob',
          status: 'Failed',
          error: 'Upstream API returned 503',
          refireCount: 1,
          durationMs: 2500,
          instanceName: 'node-2',
        }),
        execution(1),
      ],
      nextBefore: null,
    }
    render(<Harness initial={all} />)

    const row = within((await screen.findByText('invoices')).closest('tr') as HTMLElement)
    expect(row.getByText('billing · InvoiceJob · refire 1')).toBeInTheDocument()
    expect(row.getByText('Upstream API returned 503')).toBeInTheDocument()
    expect(row.getByText('Failed')).toBeInTheDocument()
    expect(row.getByText('2.5s')).toBeInTheDocument()
    expect(row.getByText('node-2')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Load older runs' })).toBeNull()
  })

  it('shows retry attempts, and runs a failed job again from its run', async () => {
    pages.all = {
      records: [
        execution(3, { status: 'Failed', attempt: 3, fireInstanceId: 'fire-3' }),
        execution(2, { status: 'Retried', attempt: 2 }),
        execution(1, { status: 'Succeeded', jobName: 'other' }),
      ],
      nextBefore: null,
    }
    const onRetry = vi.fn(async () => true)
    render(<Harness initial={all} onRetry={onRetry} />)

    expect(await screen.findByText('DEFAULT · - · attempt 3')).toBeInTheDocument()
    expect(screen.getByText('Retried', { selector: '[data-slot=badge]' })).toBeInTheDocument()
    // Only the last attempt can be run again: a retried run already has its next attempt scheduled
    const buttons = screen.getAllByRole('button', { name: /Run .* again/ })
    expect(buttons).toHaveLength(1)
    await userEvent.click(buttons[0])
    expect(onRetry).toHaveBeenCalledWith(expect.objectContaining({ fireInstanceId: 'fire-3' }))
  })

  it('has no Run again button without permission to manage jobs', async () => {
    pages.all = { records: [execution(1, { status: 'Failed' })], nextBefore: null }
    render(<Harness initial={all} />)

    expect(await screen.findByText('Failed')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /again/ })).toBeNull()
  })

  it('shows a misfire at the fire time it missed, with what Quartz did, and filters by it', async () => {
    const minutesAgo = (m: number) => new Date(Date.now() - m * 60_000).toISOString()
    const note =
      'Missed its fire time by 25m 0s; Quartz skips it and waits for the next fire time (misfire instruction DoNothing).'
    pages.Misfired = {
      records: [
        execution(4, {
          status: 'Misfired',
          scheduledFireTimeUtc: minutesAgo(25),
          firedTimeUtc: minutesAgo(0),
          error: note,
        }),
      ],
      nextBefore: null,
    }
    render(<Harness initial={all} onRetry={vi.fn(async () => true)} />)
    await screen.findByText('Runs appear here as soon as jobs execute.')

    await userEvent.click(screen.getByRole('combobox', { name: 'Filter by outcome' }))
    await userEvent.click(screen.getByRole('option', { name: 'Misfired' }))

    const row = within((await screen.findByText(note)).closest('tr') as HTMLElement)
    expect(query(1)).toMatchObject({ status: 'Misfired' })
    expect(row.getByText('Misfired', { selector: '[data-slot=badge]' })).toBeInTheDocument()
    expect(row.getByText('missed 25 min. ago')).toBeInTheDocument()
    // Not a failure, and not a run to repeat
    expect(row.getByText(note)).not.toHaveClass('text-destructive')
    expect(row.queryByRole('button', { name: /again/ })).toBeNull()
  })

  it('opens the job from its name', async () => {
    pages.all = {
      records: [execution(1, { jobGroup: 'billing', jobName: 'invoices' })],
      nextBefore: null,
    }
    render(<Harness initial={all} />)

    await userEvent.click(await screen.findByRole('button', { name: 'invoices' }))

    expect(onOpenJob).toHaveBeenCalledWith('billing', 'invoices')
  })

  it('filters by outcome', async () => {
    pages.Failed = { records: [execution(9, { status: 'Failed' })], nextBefore: null }
    render(<Harness initial={all} />)
    expect(await screen.findByText('Runs appear here as soon as jobs execute.')).toBeInTheDocument()

    await userEvent.click(screen.getByRole('combobox', { name: 'Filter by outcome' }))
    await userEvent.click(screen.getByRole('option', { name: 'Failed' }))

    expect(await screen.findByText('Failed', { selector: '[data-slot=badge]' })).toBeInTheDocument()
    expect(query(1)).toMatchObject({ status: 'Failed' })
  })

  it('shows the job filter as a chip that can be cleared', async () => {
    render(<Harness initial={{ status: 'all', job: { group: 'billing', name: 'invoices' } }} />)
    expect(await screen.findByText('Nothing matches these filters.')).toBeInTheDocument()
    expect(query(0)).toMatchObject({ group: 'billing', name: 'invoices' })

    await userEvent.click(screen.getByRole('button', { name: 'Show all jobs' }))

    expect(screen.queryByText('billing.invoices')).toBeNull()
    expect(query(1)).not.toHaveProperty('group')
  })

  it('loads older runs', async () => {
    pages.all = { records: [execution(5, { jobName: 'newer' })], nextBefore: 5 }
    pages['5'] = { records: [execution(4, { jobName: 'older' })], nextBefore: null }
    render(<Harness initial={all} />)

    await userEvent.click(await screen.findByRole('button', { name: 'Load older runs' }))

    expect(await screen.findByText('older')).toBeInTheDocument()
    expect(screen.getByText('newer')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Load older runs' })).toBeNull()
  })

  it('marks a run that started late, and one run with its own data, listing its values', async () => {
    pages.all = {
      records: [
        execution(2, {
          scheduledFireTimeUtc: '2030-01-01T00:00:00Z',
          firedTimeUtc: '2030-01-01T00:00:30Z',
        }),
        execution(1, { jobName: 'other', data: { region: 'us', cursor: '5' } }),
      ],
      nextBefore: null,
    }
    render(<Harness initial={all} />)

    expect(await screen.findByText(/started 30s late/)).toBeInTheDocument()
    expect(screen.getByText('own data')).toHaveAttribute('title', 'region = us\ncursor = 5')
  })

  it('shows the figures for the job and period it is filtered to, over every outcome', async () => {
    render(<Harness initial={{ status: 'all', job: { group: 'billing', name: 'invoices' } }} />)

    const figures = within(await screen.findByRole('region', { name: 'Run statistics' }))
    const figure = (label: string) => figures.getByText(label).nextElementSibling?.textContent
    expect(await figures.findByText('40')).toBeInTheDocument()
    expect(figure('Success rate')).toBe('92.5%')
    // Failed and blocked
    expect(figure('Failures')).toBe('3')
    expect(figure('Average duration')).toBe('1.2s')
    expect(figure('P95 duration')).toBe('4.5s')
    expect(figures.getByText(/3 retries · 1 blocked/)).toBeInTheDocument()
    const stats = (n: number) =>
      Object.fromEntries(new URLSearchParams(calls('history/stats')[n].split('?')[1]))
    expect(stats(0)).toEqual({ hours: '24', group: 'billing', name: 'invoices' })

    await userEvent.click(screen.getByRole('combobox', { name: 'Period of the figures' }))
    await userEvent.click(await screen.findByRole('option', { name: 'Last 7 days' }))
    expect(stats(calls('history/stats').length - 1)).toMatchObject({ hours: '168' })

    // The outcome filter is the list's: a success rate over failures alone says nothing
    await userEvent.click(screen.getByRole('combobox', { name: 'Filter by outcome' }))
    await userEvent.click(await screen.findByRole('option', { name: 'Failed' }))
    expect(calls('history/stats').every(path => !path.includes('status'))).toBe(true)
  })

  describe('run log', () => {
    const log = (id: number, answer: (signal: AbortSignal) => Promise<unknown>) => {
      const fallback = vi.mocked(api).getMockImplementation()!
      vi.mocked(api).mockImplementation(async (path, options) =>
        path === `history/${id}/log` ? answer(options!.signal!) : fallback(path, options)
      )
    }

    it('shows what a run logged once its log is opened, and hides it again', async () => {
      pages.all = { records: [execution(5, { hasLog: true }), execution(4)], nextBefore: null }
      log(5, async () => [
        {
          timeUtc: '2030-01-01T00:00:01Z',
          level: 'Information',
          category: 'Acme',
          message: 'Started',
        },
        {
          timeUtc: '2030-01-01T00:00:02Z',
          level: 'Warning',
          category: 'Acme',
          message: 'Slow API',
        },
        { timeUtc: '2030-01-01T00:00:03Z', level: 'Error', category: 'Acme', message: 'Gave up' },
      ])
      render(<Harness initial={all} onRetry={vi.fn(async () => true)} />)

      // Only a run whose log was kept offers one
      expect(await screen.findAllByRole('button', { name: 'log' })).toHaveLength(1)
      await userEvent.click(screen.getByRole('button', { name: 'log' }))

      const lines = within(await screen.findByRole('list', { name: 'Run log' })).getAllByRole(
        'listitem'
      )
      expect(lines.map(line => line.textContent)).toEqual([
        expect.stringMatching(/Info\s*Started$/),
        expect.stringMatching(/Warning\s*Slow API$/),
        expect.stringMatching(/Error\s*Gave up$/),
      ])
      expect(screen.getByRole('button', { name: 'hide log' })).toHaveAttribute(
        'aria-expanded',
        'true'
      )

      await userEvent.click(screen.getByRole('button', { name: 'hide log' }))
      expect(screen.queryByRole('list', { name: 'Run log' })).toBeNull()
    })

    it('says while the log loads, and why it could not be loaded', async () => {
      pages.all = { records: [execution(5, { hasLog: true })], nextBefore: null }
      let fail!: (err: Error) => void
      log(5, () => new Promise((_, reject) => (fail = reject)))
      render(<Harness initial={all} />)

      await userEvent.click(await screen.findByRole('button', { name: 'log' }))
      expect(screen.getByText('Loading the log…')).toBeInTheDocument()
      await act(async () => fail(new Error('The log of run 5 was removed')))

      expect(screen.getByText('The log of run 5 was removed')).toBeInTheDocument()
      expect(screen.queryByText('Loading the log…')).toBeNull()
    })

    it('stops loading a log closed before it arrives, and loads it again when reopened', async () => {
      pages.all = { records: [execution(5, { hasLog: true })], nextBefore: null }
      const signals: AbortSignal[] = []
      log(5, signal => {
        signals.push(signal)
        if (signals.length > 1)
          return Promise.resolve([
            {
              timeUtc: '2030-01-01T00:00:01Z',
              level: 'Information',
              category: 'Acme',
              message: 'Started',
            },
          ])
        // As fetch does when its request is aborted
        return new Promise((_, reject) =>
          signal.addEventListener('abort', () =>
            reject(new DOMException('The operation was aborted.', 'AbortError'))
          )
        )
      })
      render(<Harness initial={all} />)

      await userEvent.click(await screen.findByRole('button', { name: 'log' }))
      await userEvent.click(screen.getByRole('button', { name: 'hide log' }))
      expect(signals[0].aborted).toBe(true)

      await userEvent.click(screen.getByRole('button', { name: 'log' }))
      expect(await screen.findByRole('list', { name: 'Run log' })).toBeInTheDocument()
      expect(signals).toHaveLength(2)
    })
    it("reads a running run's log so far until it ends", async () => {
      vi.useFakeTimers({ shouldAdvanceTime: true })
      const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime })
      pages.all = {
        records: [execution(9, { status: 'Running', fireInstanceId: 'fire 9', durationMs: null })],
        nextBefore: null,
      }
      const entry = (message: string) => ({
        timeUtc: '2030-01-01T00:00:01Z',
        level: 'Information' as const,
        category: 'Acme',
        message,
      })
      let answer: () => Promise<unknown> = async () => [entry('Started')]
      const fallback = vi.mocked(api).getMockImplementation()!
      vi.mocked(api).mockImplementation(async (path, options) =>
        path === 'running/fire%209/log' ? answer() : fallback(path, options)
      )
      render(<Harness initial={all} />)

      await user.click(await screen.findByRole('button', { name: 'log so far' }))
      const log = await screen.findByRole('list', { name: 'Run log so far' })
      expect(within(log).getAllByRole('listitem')).toHaveLength(1)

      answer = async () => [entry('Started'), entry('Halfway')]
      await act(() => vi.advanceTimersByTimeAsync(2000))
      expect(within(log).getAllByRole('listitem')).toHaveLength(2)

      // A read that fails for a moment keeps what was read, and the next one tries again
      answer = async () => {
        throw new ApiError('Bad gateway', 502)
      }
      await act(() => vi.advanceTimersByTimeAsync(2000))
      expect(
        screen.getByText(/Could not read the log just now \(Bad gateway\)/)
      ).toBeInTheDocument()
      expect(within(log).getAllByRole('listitem')).toHaveLength(2)
      answer = async () => [entry('Started'), entry('Halfway'), entry('Nearly')]
      await act(() => vi.advanceTimersByTimeAsync(2000))
      expect(within(log).getAllByRole('listitem')).toHaveLength(3)
      expect(screen.queryByText(/Could not read the log just now/)).toBeNull()

      // Gone from this node: no more to read
      answer = async () => {
        throw new ApiError('Run fire 9 is not running on this node. It may have finished.', 404)
      }
      await act(() => vi.advanceTimersByTimeAsync(2000))
      expect(screen.getByText(/It may have finished/)).toBeInTheDocument()
      const reads = calls('running/').length
      await act(() => vi.advanceTimersByTimeAsync(6000))
      expect(calls('running/')).toHaveLength(reads)
    })

    it('does not read a live log again while a read is still waiting', async () => {
      vi.useFakeTimers({ shouldAdvanceTime: true })
      const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime })
      pages.all = {
        records: [execution(9, { status: 'Running', durationMs: null })],
        nextBefore: null,
      }
      let answer!: (entries: unknown) => void
      const fallback = vi.mocked(api).getMockImplementation()!
      vi.mocked(api).mockImplementation(async (path, options) =>
        (path as string).startsWith('running/')
          ? new Promise(resolve => (answer = resolve))
          : fallback(path, options)
      )
      render(<Harness initial={all} />)

      await user.click(await screen.findByRole('button', { name: 'log so far' }))
      await act(() => vi.advanceTimersByTimeAsync(7000))
      expect(calls('running/')).toHaveLength(1)
      await act(async () => answer([]))
      expect(screen.getByText('Nothing logged yet.')).toBeInTheDocument()
    })

    it('shows the saved log once a run it showed live has finished', async () => {
      vi.useFakeTimers({ shouldAdvanceTime: true })
      const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime })
      pages.all = {
        records: [execution(9, { status: 'Running', durationMs: null })],
        nextBefore: null,
      }
      const fallback = vi.mocked(api).getMockImplementation()!
      vi.mocked(api).mockImplementation(async (path, options) => {
        if ((path as string).startsWith('running/'))
          throw new ApiError('Run fire-9 is not running on this node. It may have finished.', 404)
        if (path === 'history/9/log')
          return [
            {
              timeUtc: '2030-01-01T00:00:01Z',
              level: 'Information',
              category: 'Acme',
              message: 'All done',
            },
          ]
        return fallback(path, options)
      })
      render(<Harness initial={all} />)
      await user.click(await screen.findByRole('button', { name: 'log so far' }))
      expect(await screen.findByText(/It may have finished/)).toBeInTheDocument()

      // The next refresh finds it finished, with its log
      pages.all = { records: [execution(9, { hasLog: true })], nextBefore: null }
      await act(() => vi.advanceTimersByTimeAsync(10_000))
      expect(await screen.findByText('All done')).toBeInTheDocument()
      expect(screen.queryByText(/It may have finished/)).toBeNull()
    })

    it('offers no log so far for a run on another node, which has it', async () => {
      pages.all = {
        records: [execution(9, { status: 'Running', durationMs: null, instanceName: 'node-2' })],
        nextBefore: null,
      }
      render(<Harness initial={all} />)

      expect(await screen.findByText('node-2')).toBeInTheDocument()
      expect(screen.queryByRole('button', { name: 'log so far' })).toBeNull()
    })
  })

  it('shows a load error', async () => {
    vi.mocked(api).mockImplementation(async path => {
      if ((path as string).startsWith('history/stats')) return historyStats()
      throw new Error('History table is missing')
    })
    render(<Harness initial={all} />)

    expect(await screen.findByText('History table is missing')).toBeInTheDocument()
  })

  it('looks again a second after running a job again, unless the table is gone by then', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true })
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime })
    pages.all = { records: [execution(3, { status: 'Failed' })], nextBefore: null }
    const { unmount } = render(<Harness initial={all} onRetry={vi.fn(async () => true)} />)
    const runAgain = await screen.findByRole('button', { name: /Run .* again/ })
    const requests = () => calls('history?').length

    await user.click(runAgain)
    const before = requests()
    await act(() => vi.advanceTimersByTimeAsync(1000))
    expect(requests()).toBe(before + 1)

    await user.click(runAgain)
    unmount()
    await act(() => vi.advanceTimersByTimeAsync(1000))
    expect(requests()).toBe(before + 1)
  })

  describe('while the pointer is over it', () => {
    async function pointAtThenPoll() {
      vi.useFakeTimers({ shouldAdvanceTime: true })
      const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime })
      pages.all = { records: [execution(1, { jobName: 'older' })], nextBefore: null }
      render(<Harness initial={all} />)
      await user.hover(await screen.findByText('older'))
      pages.all = {
        records: [execution(2, { jobName: 'newer' }), execution(1, { jobName: 'older' })],
        nextBefore: null,
      }
      await act(() => vi.advanceTimersByTimeAsync(10_000))
      return user
    }

    it('keeps new runs aside, so a click lands on the run aimed at', async () => {
      const user = await pointAtThenPoll()

      expect(screen.queryByText('newer')).toBeNull()
      await user.click(screen.getByRole('button', { name: 'Show 1 new run' }))

      expect(screen.getByText('newer')).toBeInTheDocument()
      expect(screen.queryByRole('button', { name: /new run/ })).toBeNull()
    })

    it('shows the new runs once the pointer leaves', async () => {
      const user = await pointAtThenPoll()

      await user.unhover(screen.getByText('older'))

      expect(screen.getByText('newer')).toBeInTheDocument()
    })

    it('dims a run no longer listed where it is, and removes it once the pointer leaves', async () => {
      vi.useFakeTimers({ shouldAdvanceTime: true })
      const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime })
      pages.all = {
        records: [execution(2, { jobName: 'finished' }), execution(1, { jobName: 'older' })],
        nextBefore: null,
      }
      render(<Harness initial={all} />)
      await user.hover(await screen.findByText('older'))
      pages.all = { records: [execution(1, { jobName: 'older' })], nextBefore: null }
      await act(() => vi.advanceTimersByTimeAsync(10_000))

      const row = screen.getByText('finished').closest('tr')
      expect(row).toHaveClass('opacity-50')
      expect(row).toHaveAttribute('title', expect.stringMatching(/No longer listed/))
      await user.unhover(screen.getByText('older'))

      expect(screen.queryByText('finished')).toBeNull()
    })
  })
})
