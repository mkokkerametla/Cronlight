import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useState } from 'react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { api } from '@/api'
import { JobSheet } from '@/components/jobs/job-sheet'
import type { DashboardActions } from '@/hooks/use-dashboard'
import { execution, job, trigger } from '@/test/fixtures'
import type { HistoryPage, Job } from '@/types'

// Recent runs and the cron preview ask the server
vi.mock('@/api', () => ({ api: vi.fn() }))

let history: HistoryPage

function renderSheet(subject: Job, overrides: Partial<Parameters<typeof JobSheet>[0]> = {}) {
  const actions = {
    run: vi.fn(async () => true),
    remove: vi.fn(async () => true),
    update: vi.fn(async () => true),
  } as unknown as DashboardActions
  const onClose = vi.fn()
  const onViewHistory = vi.fn()

  // Open state lives with the caller, as in App
  function Harness() {
    const [selected, setSelected] = useState<Job | null>(subject)
    return (
      <JobSheet
        job={selected}
        jobType={undefined}
        canManage
        historyEnabled={false}
        showScheduleAssistant={false}
        secretReferences={false}
        actions={actions}
        onClose={() => {
          onClose()
          setSelected(null)
        }}
        onViewHistory={onViewHistory}
        {...overrides}
      />
    )
  }
  render(<Harness />)
  return { actions, onClose, onViewHistory }
}

const sheet = (name = 'nightly') => within(screen.getByRole('dialog', { name }))

describe('JobSheet', () => {
  beforeEach(() => {
    history = { records: [], nextBefore: null }
    vi.mocked(api).mockImplementation(async path =>
      (path as string).startsWith('history')
        ? history
        : {
            isValid: true,
            expression: '0 30 7 * * ?',
            description: null,
            nextFireTimes: [],
            errorMessage: null,
          }
    )
  })

  it('shows the job, its triggers and its data', () => {
    renderSheet(
      job({
        description: 'Builds the nightly report',
        displayName: 'Nightly report',
        durable: false,
        data: { region: 'eu-west', note: '' },
        triggers: [
          trigger({
            name: 'weekdays',
            cronExpression: '0 0 6 ? * MON-FRI',
            timeZone: 'Europe/Paris',
          }),
          trigger({
            name: 'once',
            kind: 'Simple',
            cronExpression: null,
            timeZone: null,
            nextFireTimeUtc: null,
          }),
        ],
      })
    )

    expect(sheet().getByText('DEFAULT · Nightly report')).toBeInTheDocument()
    expect(sheet().getByText('Builds the nightly report')).toBeInTheDocument()
    expect(sheet().getByText('Triggers (2)')).toBeInTheDocument()
    expect(sheet().getByText('0 0 6 ? * MON-FRI')).toBeInTheDocument()
    expect(sheet().getByText('Europe/Paris')).toBeInTheDocument()
    expect(sheet().getByText('Not scheduled')).toBeInTheDocument()
    expect(sheet().getByText('eu-west')).toBeInTheDocument()
    expect(sheet().getByText('note').nextSibling).toHaveTextContent('-')
  })

  it('says when a job only runs manually', () => {
    renderSheet(job({ triggers: [] }))

    expect(sheet().getByText(/No triggers/)).toBeInTheDocument()
    expect(sheet().getByRole('button', { name: /Pause/ })).toBeDisabled()
  })

  it('runs, pauses and resumes the job', async () => {
    const { actions } = renderSheet(job())
    await userEvent.click(sheet().getByRole('button', { name: /Run now/ }))
    await userEvent.click(sheet().getByRole('button', { name: /Pause/ }))

    expect(actions.run).toHaveBeenNthCalledWith(
      1,
      expect.objectContaining({ name: 'nightly' }),
      'trigger'
    )
    expect(actions.run).toHaveBeenNthCalledWith(2, expect.anything(), 'pause')
  })

  it('offers Resume instead of Pause for a paused job', async () => {
    const { actions } = renderSheet(job({ triggers: [trigger({ state: 'Paused' })] }))

    expect(sheet().queryByRole('button', { name: /Pause/ })).toBeNull()
    await userEvent.click(sheet().getByRole('button', { name: /Resume/ }))
    expect(actions.run).toHaveBeenCalledWith(expect.anything(), 'resume')
  })

  it('has no controls without permission to manage jobs', () => {
    renderSheet(job(), { canManage: false })

    expect(sheet().queryByRole('button', { name: /Run now|Edit|Delete/ })).toBeNull()
  })

  it('explains an unloadable job and offers no controls even to managers', () => {
    renderSheet(job({ loadable: false, jobType: null, loadError: 'Could not load Acme.Jobs.Gone' }))

    expect(sheet().getByText('Read-only in this dashboard')).toBeInTheDocument()
    expect(sheet().getByText('Could not load Acme.Jobs.Gone')).toBeInTheDocument()
    expect(sheet().getByText('DEFAULT · type not loaded')).toBeInTheDocument()
    expect(sheet().queryByRole('button', { name: /Edit/ })).toBeNull()
  })

  it('edits the description, schedule and data, then returns to the details', async () => {
    const { actions } = renderSheet(
      job({
        description: 'old',
        data: { region: 'eu-west' },
        triggers: [
          trigger({ name: 'daily' }),
          trigger({ name: 'once', kind: 'Simple', cronExpression: null }),
        ],
      })
    )
    await userEvent.click(sheet().getByRole('button', { name: /Edit/ }))
    expect(sheet().getByText('1 non-cron trigger(s) are left unchanged.')).toBeInTheDocument()

    await userEvent.clear(sheet().getByLabelText('Description'))
    await userEvent.type(sheet().getByLabelText('Description'), 'new')
    await userEvent.clear(sheet().getByLabelText('Cron expression'))
    await userEvent.type(sheet().getByLabelText('Cron expression'), '0 30 7 * * ?')
    await userEvent.clear(sheet().getByLabelText('Value for region'))
    await userEvent.type(sheet().getByLabelText('Value for region'), 'us-east')
    await userEvent.click(sheet().getByRole('button', { name: 'Save changes' }))

    expect(actions.update).toHaveBeenCalledWith(expect.objectContaining({ name: 'nightly' }), {
      description: 'new',
      data: { region: 'us-east' },
      // A UTC trigger is edited with an empty zone, which the server reads as UTC
      triggers: [{ name: 'daily', cronExpression: '0 30 7 * * ?', timeZone: null }],
    })
    expect(sheet().queryByRole('button', { name: 'Save changes' })).toBeNull()
    expect(sheet().getByRole('button', { name: /Edit/ })).toBeInTheDocument()
  })

  it('keeps editing when the save fails, and Cancel discards the changes', async () => {
    const { actions } = renderSheet(job({ description: 'old' }))
    vi.mocked(actions.update).mockResolvedValue(false)
    await userEvent.click(sheet().getByRole('button', { name: /Edit/ }))
    await userEvent.type(sheet().getByLabelText('Description'), ' and new')

    await userEvent.click(sheet().getByRole('button', { name: 'Save changes' }))
    expect(sheet().getByLabelText('Description')).toHaveValue('old and new')

    await userEvent.click(sheet().getByRole('button', { name: 'Cancel' }))
    expect(sheet().getByText('old')).toBeInTheDocument()
  })

  it('deletes only after confirmation, then closes', async () => {
    const { actions, onClose } = renderSheet(
      job({ triggers: [trigger(), trigger({ name: 't2' })] })
    )

    await userEvent.click(sheet().getByRole('button', { name: /Delete/ }))
    const confirm = within(screen.getByRole('alertdialog'))
    expect(confirm.getByText(/all 2 of its triggers are removed/)).toBeInTheDocument()
    await userEvent.click(confirm.getByRole('button', { name: 'Cancel' }))
    expect(actions.remove).not.toHaveBeenCalled()

    await userEvent.click(sheet().getByRole('button', { name: /Delete/ }))
    await userEvent.click(screen.getByRole('button', { name: 'Delete job' }))
    expect(actions.remove).toHaveBeenCalledOnce()
    await waitFor(() => expect(onClose).toHaveBeenCalled())
  })

  it('leaves edit mode when closed, so the next job opens on its details', async () => {
    const { onClose } = renderSheet(job())
    await userEvent.click(sheet().getByRole('button', { name: /Edit/ }))

    await userEvent.keyboard('{Escape}')

    expect(onClose).toHaveBeenCalled()
    expect(screen.queryByRole('dialog')).toBeNull()
  })

  it('shows the last five runs of this job, with errors, and links to the full history', async () => {
    history = {
      records: [
        execution(2, { status: 'Failed', error: 'Upstream API returned 503' }),
        execution(1),
      ],
      nextBefore: null,
    }
    const { onViewHistory } = renderSheet(job({ group: 'billing' }), { historyEnabled: true })

    expect(await sheet().findByText('Upstream API returned 503')).toBeInTheDocument()
    expect(sheet().getByText('Failed')).toBeInTheDocument()
    expect(sheet().getByText('Succeeded')).toBeInTheDocument()
    const [path] = vi.mocked(api).mock.calls[0] as [string]
    expect(Object.fromEntries(new URLSearchParams(path.split('?')[1]))).toMatchObject({
      limit: '5',
      group: 'billing',
      name: 'nightly',
    })

    await userEvent.click(sheet().getByRole('button', { name: /All runs/ }))
    expect(onViewHistory).toHaveBeenCalledWith('billing', 'nightly')
  })

  it('says when a job has not run yet', async () => {
    renderSheet(job(), { historyEnabled: true })
    expect(await sheet().findByText('No runs recorded yet.')).toBeInTheDocument()
  })

  it('does not ask for runs when history is off', () => {
    renderSheet(job(), { historyEnabled: false })

    expect(sheet().queryByText('Recent runs')).toBeNull()
    expect(api).not.toHaveBeenCalled()
  })
})
