import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { JobsTable } from '@/components/jobs/jobs-table'
import { TooltipProvider } from '@/components/ui/tooltip'
import type { DashboardActions } from '@/hooks/use-dashboard'
import { job, trigger } from '@/test/fixtures'
import type { Job } from '@/types'

const jobs: Job[] = [
  job({ name: 'send-invoices', group: 'billing', displayName: 'Send invoices' }),
  job({ name: 'cleanup', group: 'maintenance', triggers: [trigger({ state: 'Paused' })] }),
  job({
    name: 'foreign',
    group: 'other',
    loadable: false,
    jobType: null,
    loadError: 'Type not found',
  }),
]

function renderTable(overrides: Partial<Parameters<typeof JobsTable>[0]> = {}) {
  const actions = { run: vi.fn(), bulk: vi.fn(async () => {}) } as unknown as DashboardActions
  const onSelect = vi.fn()
  render(
    <TooltipProvider>
      <JobsTable
        jobs={jobs}
        canManage
        canCreate
        statusFilter="all"
        onStatusFilterChange={vi.fn()}
        selectedKey={null}
        onSelect={onSelect}
        onNewJob={vi.fn()}
        actions={actions}
        {...overrides}
      />
    </TooltipProvider>
  )
  return { actions, onSelect }
}

const row = (name: string) =>
  screen.getByText(name, { selector: 'div' }).closest('tr') as HTMLElement
const names = () =>
  screen
    .queryAllByRole('row')
    .slice(1)
    .map(r => within(r).getAllByRole('cell')[1].firstChild?.textContent)

describe('JobsTable', () => {
  it('lists jobs with their type and status', () => {
    renderTable()

    expect(within(row('send-invoices')).getByText('Send invoices')).toBeInTheDocument()
    expect(within(row('cleanup')).getByText('Paused')).toBeInTheDocument()
    expect(within(row('foreign')).getByText('Unloadable')).toBeInTheDocument()
  })

  it('searches by name or group', async () => {
    renderTable()

    await userEvent.type(screen.getByLabelText('Search jobs'), 'billing')
    expect(names()).toEqual(['send-invoices'])

    await userEvent.clear(screen.getByLabelText('Search jobs'))
    await userEvent.type(screen.getByLabelText('Search jobs'), 'nothing-matches')
    expect(screen.getByText('No jobs match these filters.')).toBeInTheDocument()
  })

  it('applies the status filter it is given', () => {
    renderTable({ statusFilter: 'Paused' })

    expect(names()).toEqual(['cleanup'])
  })

  it('runs, pauses and resumes a job from its row', async () => {
    const { actions } = renderTable()

    await userEvent.click(screen.getByRole('button', { name: 'Run send-invoices now' }))
    await userEvent.click(screen.getByRole('button', { name: 'Pause send-invoices' }))
    await userEvent.click(screen.getByRole('button', { name: 'Resume cleanup' }))

    expect(vi.mocked(actions.run).mock.calls.map(([j, action]) => [j.name, action])).toEqual([
      ['send-invoices', 'trigger'],
      ['send-invoices', 'pause'],
      ['cleanup', 'resume'],
    ])
  })

  it('pauses the selected jobs in bulk, never offering jobs from other apps', async () => {
    const { actions } = renderTable()
    expect(within(row('foreign')).getByRole('checkbox')).toBeDisabled()

    await userEvent.click(screen.getByRole('checkbox', { name: 'Select all' }))
    expect(screen.getByText('2 selected')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: /^Pause$/ }))

    expect(vi.mocked(actions.bulk)).toHaveBeenCalledWith('pause', expect.any(Array))
    expect(
      vi
        .mocked(actions.bulk)
        .mock.calls[0][1].map(j => j.name)
        .sort()
    ).toEqual(['cleanup', 'send-invoices'])
    expect(screen.queryByText(/selected$/)).toBeNull()
  })

  it('opens a job when its row is clicked', async () => {
    const { onSelect } = renderTable()

    await userEvent.click(row('cleanup'))

    expect(onSelect).toHaveBeenCalledWith(expect.objectContaining({ name: 'cleanup' }))
  })

  it('shows no controls that change jobs to a read-only user', () => {
    renderTable({ canManage: false })

    expect(screen.queryByRole('checkbox')).toBeNull()
    expect(screen.queryByRole('button', { name: /New job|Run |Pause |Resume / })).toBeNull()
    expect(names()).toHaveLength(3)
  })

  it('says when the scheduler has no jobs', () => {
    renderTable({ jobs: [] })

    expect(screen.getByText('This scheduler has no jobs yet.')).toBeInTheDocument()
  })
})
