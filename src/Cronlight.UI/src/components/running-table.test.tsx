import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { api } from '@/api'
import { RunningTable } from '@/components/running-table'
import { TooltipProvider } from '@/components/ui/tooltip'
import type { RunningJob, RunningJobs } from '@/types'

vi.mock('@/api', async importOriginal => ({
  ...(await importOriginal<typeof import('@/api')>()),
  api: vi.fn(),
}))

const renderTable = (
  running: RunningJobs,
  props: Partial<Parameters<typeof RunningTable>[0]> = {}
) =>
  render(
    <TooltipProvider delayDuration={0}>
      <RunningTable running={running} {...props} />
    </TooltipProvider>
  )

const run = (overrides: Partial<RunningJob> = {}): RunningJob => ({
  name: 'invoices',
  group: 'billing',
  jobType: 'Acme.Jobs.InvoiceJob',
  triggerName: 't1',
  triggerGroup: 'DEFAULT',
  fireInstanceId: 'fire-1',
  instanceName: 'node-1',
  firedTimeUtc: '2030-01-01T09:00:00Z',
  runningForMs: 75_000,
  ...overrides,
})

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

  it('cancels a run on this node after confirmation', async () => {
    const onCancel = vi.fn(async () => true)
    renderTable(
      { source: 'cluster', jobs: [run()] },
      { canManage: true, localInstanceId: 'node-1', onCancel }
    )

    await userEvent.click(screen.getByRole('button', { name: 'Cancel invoices' }))
    const dialog = screen.getByRole('alertdialog')
    expect(dialog).toHaveTextContent(/stops when it next checks for cancellation/)
    await userEvent.click(within(dialog).getByRole('button', { name: 'Keep running' }))
    expect(onCancel).not.toHaveBeenCalled()

    await userEvent.click(screen.getByRole('button', { name: 'Cancel invoices' }))
    await userEvent.click(screen.getByRole('button', { name: 'Cancel run' }))
    expect(onCancel).toHaveBeenCalledWith(expect.objectContaining({ fireInstanceId: 'fire-1' }))
  })

  it('explains that a run on another node can only be cancelled there', async () => {
    renderTable(
      { source: 'cluster', jobs: [run({ instanceName: 'node-2' })] },
      { canManage: true, localInstanceId: 'node-1', onCancel: vi.fn() }
    )

    const button = screen.getByRole('button', { name: 'Cancel invoices' })
    expect(button).toBeDisabled()
    await userEvent.hover(button.parentElement!)
    expect(await screen.findByRole('tooltip')).toHaveTextContent(/Runs on node-2/)
  })

  it('offers no cancel button to people who cannot manage jobs', () => {
    renderTable({ source: 'local', jobs: [run()] }, { canManage: false, onCancel: vi.fn() })

    expect(screen.queryByRole('button', { name: 'Cancel invoices' })).not.toBeInTheDocument()
  })

  describe('log so far', () => {
    it('shows what a run on this node has logged so far', async () => {
      vi.mocked(api).mockResolvedValue([
        {
          timeUtc: '2030-01-01T09:00:01Z',
          level: 'Information',
          category: 'Acme',
          message: 'Sent 40 of 120',
        },
      ])
      renderTable(
        { source: 'cluster', jobs: [run({ instanceName: 'node-1' })] },
        { logs: true, localInstanceId: 'node-1' }
      )

      await userEvent.click(screen.getByRole('button', { name: 'Log of invoices' }))

      const log = await screen.findByRole('list', { name: 'Run log so far' })
      expect(within(log).getByText('Sent 40 of 120')).toBeInTheDocument()
      expect(api).toHaveBeenCalledWith('running/fire-1/log', expect.anything())
      await userEvent.click(screen.getByRole('button', { name: 'Hide the log of invoices' }))
      expect(screen.queryByRole('list', { name: 'Run log so far' })).toBeNull()
    })

    it("says that another node's run shows its log on that node", async () => {
      renderTable(
        { source: 'cluster', jobs: [run({ instanceName: 'node-2' })] },
        { logs: true, localInstanceId: 'node-1' }
      )

      const button = screen.getByRole('button', { name: 'Log of invoices' })
      expect(button).toBeDisabled()
      await userEvent.hover(button.parentElement!)
      expect(await screen.findByRole('tooltip')).toHaveTextContent(
        /Runs on node-2, which has its log so far/
      )
    })

    it('offers no log without execution history, which keeps runs logs', () => {
      renderTable({ source: 'local', jobs: [run()] })
      expect(screen.queryByRole('button', { name: 'Log of invoices' })).toBeNull()
    })
  })
})
