import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useState } from 'react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { api } from '@/api'
import { HistoryTable } from '@/components/history/history-table'
import { execution } from '@/test/fixtures'
import type { HistoryFilter, HistoryPage } from '@/types'

vi.mock('@/api', () => ({ api: vi.fn() }))

let pages: Record<string, HistoryPage>
const onOpenJob = vi.fn()

/** The table with real filter state, as App uses it. */
function Harness({ initial }: { initial: HistoryFilter }) {
  const [filter, setFilter] = useState(initial)
  return <HistoryTable filter={filter} onFilterChange={setFilter} onOpenJob={onOpenJob} />
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
})
