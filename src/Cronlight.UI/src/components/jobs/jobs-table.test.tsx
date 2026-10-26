import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { JobsTable } from '@/components/jobs/jobs-table'
import { TooltipProvider } from '@/components/ui/tooltip'
import { emptyQuery, type DashboardActions } from '@/hooks/use-dashboard'
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
  const actions = {
    run: vi.fn(),
    bulk: vi.fn(async () => {}),
    group: vi.fn(async () => true),
    all: vi.fn(async () => true),
  } as unknown as DashboardActions
  const onSelect = vi.fn()
  const onQueryChange = vi.fn()
  render(
    <TooltipProvider>
      <JobsTable
        jobs={jobs}
        page={{ total: jobs.length, page: 1, pageSize: 50, types: ['Send invoices', 'NightlyJob'] }}
        query={emptyQuery}
        onQueryChange={onQueryChange}
        canManage
        canCreate
        selectedKey={null}
        onSelect={onSelect}
        onNewJob={vi.fn()}
        actions={actions}
        {...overrides}
      />
    </TooltipProvider>
  )
  return { actions, onSelect, onQueryChange }
}

const row = (name: string) =>
  screen.getByText(name, { selector: 'div' }).closest('tr') as HTMLElement
const names = () =>
  screen
    .queryAllByRole('row')
    .slice(1)
    .map(r => within(r).getAllByRole('cell')[1].firstChild?.textContent)

describe('JobsTable', () => {
  it('lists jobs with their type, status and schedule in words', () => {
    renderTable()

    expect(within(row('send-invoices')).getByText('At 06:00')).toBeInTheDocument()
    expect(within(row('send-invoices')).getByText('Send invoices')).toBeInTheDocument()
    expect(within(row('cleanup')).getByText('Paused')).toBeInTheDocument()
    expect(within(row('foreign')).getByText('Unloadable')).toBeInTheDocument()
  })

  it('moves to the search box with /, through the jobs with j and k, and opens one with Enter', async () => {
    const { onSelect } = renderTable()
    const active = () =>
      screen
        .queryAllByRole('row')
        .filter(r => r.dataset.active === 'true')
        .map(r => within(r).getAllByRole('cell')[1].firstChild?.textContent)

    await userEvent.keyboard('j')
    expect(active()).toEqual(['send-invoices'])
    await userEvent.keyboard('jjk')
    expect(active()).toEqual(['cleanup'])
    await userEvent.keyboard('{Enter}')
    expect(onSelect).toHaveBeenCalledWith(expect.objectContaining({ name: 'cleanup' }))

    // Keys typed into the search box are text, not shortcuts
    await userEvent.keyboard('/')
    expect(screen.getByRole('textbox', { name: 'Search jobs' })).toHaveFocus()
    await userEvent.keyboard('jk')
    expect(screen.getByRole('textbox', { name: 'Search jobs' })).toHaveValue('jk')
    expect(active()).toEqual(['cleanup'])
  })

  it('searches on the server once typing pauses, from the first page', async () => {
    const { onQueryChange } = renderTable({ query: { ...emptyQuery, page: 3 } })

    await userEvent.type(screen.getByLabelText('Search jobs'), 'billing')

    await waitFor(() =>
      expect(onQueryChange).toHaveBeenCalledWith({ ...emptyQuery, search: 'billing', page: 1 })
    )
    expect(onQueryChange).toHaveBeenCalledTimes(1)
  })

  it('filters by type and status on the server, offering every type in the scheduler', async () => {
    const { onQueryChange } = renderTable()

    await userEvent.click(screen.getByRole('combobox', { name: 'Filter by type' }))
    expect(screen.getAllByRole('option').map(o => o.textContent)).toEqual([
      'All types',
      'Send invoices',
      'NightlyJob',
    ])
    await userEvent.click(screen.getByRole('option', { name: 'Send invoices' }))
    expect(onQueryChange).toHaveBeenLastCalledWith({ ...emptyQuery, type: 'Send invoices' })

    await userEvent.click(screen.getByRole('combobox', { name: 'Filter by status' }))
    await userEvent.click(screen.getByRole('option', { name: 'Paused' }))
    expect(onQueryChange).toHaveBeenLastCalledWith({ ...emptyQuery, status: 'Paused' })

    await userEvent.click(screen.getByRole('combobox', { name: 'Filter by status' }))
    await userEvent.click(screen.getByRole('option', { name: 'Error' }))
    expect(onQueryChange).toHaveBeenLastCalledWith({ ...emptyQuery, status: 'Error' })
  })

  it('shows a job Quartz stopped as Error, and resumes it from its row', async () => {
    const { actions } = renderTable({
      jobs: [job({ name: 'stuck', triggers: [trigger({ state: 'Error' })] })],
    })

    expect(within(row('stuck')).getByText('Error')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Resume stuck' }))
    expect(actions.run).toHaveBeenCalledWith(expect.objectContaining({ name: 'stuck' }), 'resume')
  })

  it('says when no jobs match, and clears the filters', async () => {
    const { onQueryChange } = renderTable({
      jobs: [],
      query: { ...emptyQuery, search: 'nothing', status: 'Paused' },
    })

    expect(screen.getByText('No jobs match these filters.')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: /Clear/ }))
    expect(onQueryChange).toHaveBeenCalledWith(emptyQuery)
  })

  it('pages through more jobs than fit on one page', async () => {
    const { onQueryChange } = renderTable({
      page: { total: 120, page: 2, pageSize: 50, types: [] },
      query: { ...emptyQuery, page: 2 },
    })

    expect(screen.getByText('51–100 of 120')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: /Next/ }))
    expect(onQueryChange).toHaveBeenLastCalledWith({ ...emptyQuery, page: 3 })
    await userEvent.click(screen.getByRole('button', { name: /Previous/ }))
    expect(onQueryChange).toHaveBeenLastCalledWith({ ...emptyQuery, page: 1 })
  })

  it('shows no pages when every job fits on one', () => {
    renderTable()

    expect(screen.queryByRole('navigation', { name: 'Job list pages' })).toBeNull()
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

  it("resumes a job's group only once asked, saying what resuming undoes", async () => {
    const { actions } = renderTable()

    await userEvent.click(screen.getByRole('button', { name: 'More actions for cleanup' }))
    await userEvent.click(await screen.findByRole('menuitem', { name: /Resume group maintenance/ }))
    const dialog = await screen.findByRole('alertdialog')
    expect(
      within(dialog).getByText(/including jobs that were paused one by one/)
    ).toBeInTheDocument()
    expect(actions.group).not.toHaveBeenCalled()

    await userEvent.click(within(dialog).getByRole('button', { name: 'Resume group' }))
    expect(actions.group).toHaveBeenCalledWith('maintenance', 'resume')
  })

  it('offers to pause a group only when it has a job running, and to resume it only when it has one paused', async () => {
    renderTable()

    // billing: its only job is active
    await userEvent.click(screen.getByRole('button', { name: 'More actions for send-invoices' }))
    expect(await screen.findByRole('menuitem', { name: /Pause group billing/ })).toBeInTheDocument()
    expect(screen.queryByRole('menuitem', { name: /Resume group billing/ })).toBeNull()
    await userEvent.keyboard('{Escape}')

    // maintenance: its only job is paused
    await userEvent.click(screen.getByRole('button', { name: 'More actions for cleanup' }))
    expect(
      await screen.findByRole('menuitem', { name: /Resume group maintenance/ })
    ).toBeInTheDocument()
    expect(screen.queryByRole('menuitem', { name: /Pause group maintenance/ })).toBeNull()
  })

  it('shows no next fire for a paused job, and an overdue one as due now', () => {
    renderTable({
      jobs: [
        job({
          name: 'held',
          triggers: [trigger({ state: 'Paused', nextFireTimeUtc: '2000-01-01T00:00:00Z' })],
        }),
        job({ name: 'late', triggers: [trigger({ nextFireTimeUtc: '2000-01-01T00:00:00Z' })] }),
      ],
    })

    const nextFireCell = (name: string) => within(row(name)).getAllByRole('cell').at(-2)
    expect(nextFireCell('held')).toHaveTextContent('-')
    expect(nextFireCell('late')).toHaveTextContent('Due now')
  })

  it('pauses every job after confirming, and cancelling does nothing', async () => {
    const { actions } = renderTable()

    await userEvent.click(screen.getByRole('button', { name: 'All jobs' }))
    await userEvent.click(await screen.findByRole('menuitem', { name: /Pause all jobs/ }))
    await userEvent.click(
      within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Cancel' })
    )
    expect(actions.all).not.toHaveBeenCalled()

    await userEvent.click(screen.getByRole('button', { name: 'All jobs' }))
    await userEvent.click(await screen.findByRole('menuitem', { name: /Pause all jobs/ }))
    await userEvent.click(
      within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Pause all jobs' })
    )
    expect(actions.all).toHaveBeenCalledWith('pause')
  })
})
