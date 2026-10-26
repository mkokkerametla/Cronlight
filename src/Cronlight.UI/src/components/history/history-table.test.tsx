import { act, render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useState } from 'react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { api } from '@/api'
import { HistoryTable } from '@/components/history/history-table'
import { execution } from '@/test/fixtures'
import type { ExecutionRecord, HistoryFilter, HistoryPage, JobType } from '@/types'

vi.mock('@/api', () => ({ api: vi.fn() }))

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
  const [filter, setFilter] = useState(initial)
  return (
    <HistoryTable
      filter={filter}
      onFilterChange={setFilter}
      onOpenJob={onOpenJob}
      canManage={onRetry !== undefined}
      onRetry={onRetry}
      jobTypes={jobTypes}
    />
  )
}

const all: HistoryFilter = { status: 'all', job: null }
const query = (call: number) =>
  Object.fromEntries(
    new URLSearchParams((vi.mocked(api).mock.calls[call][0] as string).split('?')[1])
  )

describe('HistoryTable', () => {
  beforeEach(() => {
    pages = {}
    vi.mocked(api).mockImplementation(async path => {
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

  it('shows a load error', async () => {
    vi.mocked(api).mockRejectedValue(new Error('History table is missing'))
    render(<Harness initial={all} />)

    expect(await screen.findByText('History table is missing')).toBeInTheDocument()
  })

  it('looks again a second after running a job again, unless the table is gone by then', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true })
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime })
    pages.all = { records: [execution(3, { status: 'Failed' })], nextBefore: null }
    const { unmount } = render(<Harness initial={all} onRetry={vi.fn(async () => true)} />)
    const runAgain = await screen.findByRole('button', { name: /Run .* again/ })
    const calls = () => vi.mocked(api).mock.calls.length

    await user.click(runAgain)
    const before = calls()
    await act(() => vi.advanceTimersByTimeAsync(1000))
    expect(calls()).toBe(before + 1)

    await user.click(runAgain)
    unmount()
    await act(() => vi.advanceTimersByTimeAsync(1000))
    expect(calls()).toBe(before + 1)
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
