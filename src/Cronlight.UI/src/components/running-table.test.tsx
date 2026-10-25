import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { RunningTable } from '@/components/running-table'
import { TooltipProvider } from '@/components/ui/tooltip'
import type { RunningJobs } from '@/types'

const renderTable = (running: RunningJobs) =>
  render(
    <TooltipProvider delayDuration={0}>
      <RunningTable running={running} />
    </TooltipProvider>
  )

describe('RunningTable', () => {
  it('lists running jobs with their node and how long they have run', () => {
    renderTable({
      source: 'cluster',
      jobs: [
        {
          name: 'invoices',
          group: 'billing',
          jobType: 'Acme.Jobs.InvoiceJob',
          triggerName: 't1',
          triggerGroup: 'DEFAULT',
          fireInstanceId: 'fire-1',
          instanceName: 'node-2',
          firedTimeUtc: '2030-01-01T09:00:00Z',
          runningForMs: 75_000,
        },
      ],
    })

    const row = within(screen.getAllByRole('row')[1])
    expect(row.getByText('invoices')).toBeInTheDocument()
    expect(row.getByText('billing')).toBeInTheDocument()
    expect(row.getByText('InvoiceJob')).toBeInTheDocument()
    expect(row.getByText('node-2')).toBeInTheDocument()
    expect(row.getByText('1m 15s')).toBeInTheDocument()
    expect(screen.getByText('All nodes')).toBeInTheDocument()
  })

  it('explains that an in-memory store only sees this node', async () => {
    renderTable({ source: 'local', jobs: [] })

    expect(screen.getByText('No jobs running')).toBeInTheDocument()
    await userEvent.hover(screen.getByText('This node only'))
    expect(await screen.findByRole('tooltip')).toHaveTextContent(/only jobs running on this node/)
  })
})
