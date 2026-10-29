import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useEffect, useState } from 'react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { api } from '@/api'
import { JobSheet } from '@/components/jobs/job-sheet'
import type { DashboardActions } from '@/hooks/use-dashboard'
import { formatDateTime } from '@/lib/format'
import { TooltipProvider } from '@/components/ui/tooltip'
import { execution, historyStats, job, trigger } from '@/test/fixtures'
import type { HistoryPage, Job } from '@/types'

// Recent runs, the cron preview and the next fire times ask the server
vi.mock('@/api', async importOriginal => ({
  ...(await importOriginal<typeof import('@/api')>()),
  api: vi.fn(),
}))

let history: HistoryPage

function renderSheet(subject: Job, overrides: Partial<Parameters<typeof JobSheet>[0]> = {}) {
  const actions = {
    run: vi.fn(async () => true),
    runWithData: vi.fn(async () => true),
    remove: vi.fn(async () => true),
    update: vi.fn(async () => true),
    cancelRetry: vi.fn(async () => true),
  } as unknown as DashboardActions
  const onClose = vi.fn()
  const onViewHistory = vi.fn()
  // Shows the job as the next poll returns it
  const handle: { show: (next: Job) => void } = { show: () => {} }

  // Open state lives with the caller, as in App
  function Harness() {
    const [selected, setSelected] = useState<Job | null>(subject)
    useEffect(() => {
      handle.show = setSelected
    }, [])
    return (
      <JobSheet
        job={selected}
        jobType={undefined}
        canManage
        historyEnabled={false}
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
  // As main.tsx provides it
  render(
    <TooltipProvider>
      <Harness />
    </TooltipProvider>
  )
  const poll = (next: Job) => act(() => handle.show(next))
  return { actions, onClose, onViewHistory, poll }
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

  it('lists the next five fire times of a schedule once asked, and hides them again', async () => {
    const times = [1, 2, 3, 4, 5].map(day => `2030-01-0${day}T09:00:00Z`)
    vi.mocked(api).mockImplementation(async path =>
      path === 'jobs/DEFAULT/nightly/fire-times'
        ? [
            { group: 'DEFAULT', name: 'weekdays', fireTimesUtc: times },
            { group: 'DEFAULT', name: 'launch', fireTimesUtc: ['2031-01-01T00:00:00Z'] },
          ]
        : history
    )
    renderSheet(
      job({
        triggers: [
          trigger({ name: 'weekdays', nextFireTimeUtc: times[0] }),
          // One run only, or not firing now: nothing more to list
          trigger({ name: 'launch', kind: 'Once', cronExpression: null, timeZone: null }),
          trigger({ name: 'held', state: 'Paused' }),
          trigger({ name: 'done', state: 'Complete', nextFireTimeUtc: null }),
        ],
      })
    )

    const toggle = sheet().getByRole('button', { name: 'Next 5' })
    expect(sheet().getAllByRole('button', { name: 'Next 5' })).toHaveLength(1)
    expect(toggle).toHaveAttribute('aria-expanded', 'false')
    await userEvent.click(toggle)

    const list = await sheet().findByRole('list', { name: 'Next 5 fire times of weekdays' })
    const items = within(list).getAllByRole('listitem')
    expect(items).toHaveLength(5)
    times.forEach((time, i) => expect(items[i]).toHaveTextContent(formatDateTime(time)))
    expect(toggle).toHaveAttribute('aria-expanded', 'true')

    await userEvent.click(toggle)
    expect(sheet().queryByRole('list', { name: /fire times/ })).toBeNull()
  })

  it('says when a schedule has no fire times left by the time they are listed', async () => {
    vi.mocked(api).mockImplementation(async path =>
      path === 'jobs/DEFAULT/nightly/fire-times'
        ? [{ group: 'DEFAULT', name: 't1', fireTimesUtc: [] }]
        : history
    )
    renderSheet(job())

    await userEvent.click(sheet().getByRole('button', { name: 'Next 5' }))

    expect(await sheet().findByText('It does not fire again')).toBeInTheDocument()
  })

  it('says why the next fire times could not be listed', async () => {
    vi.mocked(api).mockImplementation(async path => {
      if (path === 'jobs/DEFAULT/nightly/fire-times') throw new Error('500 Internal Server Error')
      return history
    })
    renderSheet(job())

    await userEvent.click(sheet().getByRole('button', { name: 'Next 5' }))

    expect(
      await sheet().findByText('Could not list the fire times: 500 Internal Server Error')
    ).toBeInTheDocument()
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
            calendarName: 'holidays',
          }),
          trigger({
            name: 'once',
            kind: 'Once',
            cronExpression: null,
            timeZone: null,
            nextFireTimeUtc: null,
            startTimeUtc: '2030-01-01T06:00:00Z',
          }),
        ],
      })
    )

    expect(sheet().getByText('DEFAULT · Nightly report')).toBeInTheDocument()
    expect(sheet().getByText('Builds the nightly report')).toBeInTheDocument()
    expect(sheet().getByText('Triggers (2)')).toBeInTheDocument()
    expect(sheet().getByText('0 0 6 ? * MON-FRI')).toBeInTheDocument()
    expect(sheet().getByText('Europe/Paris')).toBeInTheDocument()
    expect(sheet().getByText('Skips times excluded by').nextSibling).toHaveTextContent('holidays')
    expect(sheet().getAllByText('Skips times excluded by')).toHaveLength(1)
    expect(sheet().getByText('Not scheduled')).toBeInTheDocument()
    expect(sheet().getByText(/^Once, /)).toBeInTheDocument()
    expect(sheet().getByText('eu-west')).toBeInTheDocument()
    expect(sheet().getByText('note').nextSibling).toHaveTextContent('-')
  })

  it('shows a waiting retry with its attempt, cancels it, and says what happens to missed runs', async () => {
    const retry = trigger({
      name: 'r1',
      group: 'Cronlight.Retries',
      kind: 'Retry',
      cronExpression: null,
      attempt: 2,
      misfireInstruction: 'FireNow',
    })
    const subject = job({
      triggers: [trigger({ misfireInstruction: 'DoNothing' }), retry],
    })
    const { actions } = renderSheet(subject)

    expect(sheet().getByText('Retry, attempt 2')).toBeInTheDocument()
    expect(sheet().getByText('Skipped; waits for the next fire time')).toBeInTheDocument()
    expect(sheet().getByText('Runs once as soon as it can')).toBeInTheDocument()
    await userEvent.click(sheet().getByRole('button', { name: 'Cancel retry r1' }))
    expect(actions.cancelRetry).toHaveBeenCalledWith(subject, retry)
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

  it('explains a job Quartz stopped, and offers Resume instead of Pause', async () => {
    const { actions } = renderSheet(job({ triggers: [trigger({ state: 'Error' })] }))

    expect(sheet().getByText('Stopped by Quartz')).toBeInTheDocument()
    expect(sheet().queryByRole('button', { name: /Pause/ })).toBeNull()
    await userEvent.click(sheet().getByRole('button', { name: /Resume/ }))
    expect(actions.run).toHaveBeenCalledWith(expect.anything(), 'resume')
  })

  it('shows the retry policy and changes it when editing', async () => {
    const { actions } = renderSheet(
      job({ retry: { maxRetries: 2, delaySeconds: 90, exponential: false } })
    )
    expect(sheet().getByText('Up to 2 retries, 1m 30s apart')).toBeInTheDocument()

    await userEvent.click(sheet().getByRole('button', { name: /Edit/ }))
    await userEvent.click(sheet().getByLabelText('Double the wait after each retry'))
    await userEvent.click(sheet().getByRole('button', { name: 'Save changes' }))

    expect(actions.update).toHaveBeenCalledWith(
      expect.anything(),
      expect.objectContaining({ retry: { maxRetries: 2, delaySeconds: 90, exponential: true } })
    )
  })

  it('runs once with changed data, sending only what changed', async () => {
    const { actions } = renderSheet(job({ data: { region: 'eu', apiToken: '********' } }))

    await userEvent.click(sheet().getByRole('button', { name: /Run with different data/ }))
    expect(sheet().getByText('Nothing changed')).toBeInTheDocument()
    await userEvent.clear(sheet().getByLabelText('Value for region'))
    await userEvent.type(sheet().getByLabelText('Value for region'), 'us')
    expect(sheet().getByText('1 changed')).toBeInTheDocument()
    await userEvent.click(sheet().getByRole('button', { name: /Run now/ }))

    // The masked secret is left out, so the saved one is used
    expect(actions.runWithData).toHaveBeenCalledWith(expect.objectContaining({ name: 'nightly' }), {
      region: 'us',
    })
    expect(sheet().getByRole('button', { name: /Run with different data/ })).toBeInTheDocument()
  })

  it('returns to the details when a run with different data is cancelled, running nothing', async () => {
    const { actions } = renderSheet(job({ data: { region: 'eu' } }))

    await userEvent.click(sheet().getByRole('button', { name: /Run with different data/ }))
    await userEvent.type(sheet().getByLabelText('Value for region'), '-west')
    await userEvent.click(sheet().getByRole('button', { name: 'Cancel' }))

    expect(actions.runWithData).not.toHaveBeenCalled()
    expect(sheet().getByText('eu')).toBeInTheDocument()
    expect(sheet().getByRole('button', { name: /Run with different data/ })).toBeInTheDocument()
  })

  it('keeps the data entered when the run could not be started', async () => {
    const { actions } = renderSheet(job({ data: { region: 'eu' } }))
    vi.mocked(actions.runWithData).mockResolvedValue(false)

    await userEvent.click(sheet().getByRole('button', { name: /Run with different data/ }))
    await userEvent.clear(sheet().getByLabelText('Value for region'))
    await userEvent.type(sheet().getByLabelText('Value for region'), 'us')
    await userEvent.click(sheet().getByRole('button', { name: /Run now/ }))

    expect(actions.runWithData).toHaveBeenCalledOnce()
    expect(sheet().getByLabelText('Value for region')).toHaveValue('us')
    expect(sheet().getByRole('button', { name: /Run now/ })).toBeEnabled()
  })

  it('runs with what was changed in the form, not values the job saved meanwhile', async () => {
    const { actions, poll } = renderSheet(job({ data: { cursor: '400', region: 'eu' } }))
    await userEvent.click(sheet().getByRole('button', { name: /Run with different data/ }))

    // The job ran and saved a new cursor while the form was open
    poll(job({ data: { cursor: '500', region: 'eu' } }))
    expect(sheet().getByText('Nothing changed')).toBeInTheDocument()
    await userEvent.clear(sheet().getByLabelText('Value for region'))
    await userEvent.type(sheet().getByLabelText('Value for region'), 'us')
    await userEvent.click(sheet().getByRole('button', { name: /Run now/ }))

    expect(actions.runWithData).toHaveBeenCalledWith(expect.anything(), { region: 'us' })
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
          trigger({ name: 'retry', kind: 'Retry', cronExpression: null, attempt: 2 }),
        ],
      })
    )
    await userEvent.click(sheet().getByRole('button', { name: /Edit/ }))
    expect(
      sheet().getByText('1 other trigger(s), such as waiting retries, are left unchanged.')
    ).toBeInTheDocument()

    await userEvent.clear(sheet().getByLabelText('Description'))
    await userEvent.type(sheet().getByLabelText('Description'), 'new')
    await userEvent.clear(sheet().getByLabelText('Cron expression'))
    await userEvent.type(sheet().getByLabelText('Cron expression'), '0 30 7 * * ?')
    await userEvent.clear(sheet().getByLabelText('Value for region'))
    await userEvent.type(sheet().getByLabelText('Value for region'), 'us-east')
    await userEvent.click(sheet().getByRole('button', { name: 'Save changes' }))

    // The retry policy was left as it was, so it is not sent
    expect(actions.update).toHaveBeenCalledWith(expect.objectContaining({ name: 'nightly' }), {
      description: 'new',
      data: { region: 'us-east' },
      // A UTC trigger is edited with an empty zone, which the server reads as UTC
      triggers: [{ name: 'daily', cronExpression: '0 30 7 * * ?', timeZone: null }],
      // So a one-time or interval trigger removed in the editor is removed from the job too
      triggerKinds: ['Cron', 'Once', 'Interval'],
    })
    expect(sheet().queryByRole('button', { name: 'Save changes' })).toBeNull()
    expect(sheet().getByRole('button', { name: /Edit/ })).toBeInTheDocument()
  })

  it('saves only what was edited, so what changed meanwhile is kept', async () => {
    const { actions, poll } = renderSheet(
      job({ description: 'old', data: { cursor: '400', region: 'eu' } })
    )
    await userEvent.click(sheet().getByRole('button', { name: /Edit/ }))

    // The job ran and saved a new cursor while the form was open
    poll(job({ description: 'old', data: { cursor: '500', region: 'eu' } }))
    await userEvent.clear(sheet().getByLabelText('Description'))
    await userEvent.type(sheet().getByLabelText('Description'), 'new')
    await userEvent.click(sheet().getByRole('button', { name: 'Save changes' }))

    expect(actions.update).toHaveBeenCalledWith(expect.anything(), { description: 'new' })
  })

  it('removes a data entry deleted in the form', async () => {
    const { actions } = renderSheet(job({ data: { region: 'eu', legacy: 'x' } }))
    await userEvent.click(sheet().getByRole('button', { name: /Edit/ }))

    await userEvent.click(sheet().getByRole('button', { name: 'Remove legacy' }))
    await userEvent.click(sheet().getByRole('button', { name: 'Save changes' }))

    expect(actions.update).toHaveBeenCalledWith(expect.anything(), { data: { legacy: null } })
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

  it('shows the next fire of a paused trigger as paused, and an overdue one as due now', () => {
    renderSheet(
      job({
        triggers: [
          trigger({ name: 'held', state: 'Paused', nextFireTimeUtc: '2000-01-01T00:00:00Z' }),
          trigger({ name: 'late', nextFireTimeUtc: '2000-01-01T00:00:00Z' }),
        ],
      })
    )

    const next = sheet()
      .getAllByText('Next fire')
      .map(label => label.nextElementSibling?.textContent)
    expect(next).toEqual(['Paused', 'Due now'])
  })

  it('shows when a trigger last fired, and one that will not fire again as Complete', () => {
    renderSheet(
      job({
        triggers: [
          trigger({
            state: 'Complete',
            nextFireTimeUtc: null,
            previousFireTimeUtc: new Date(Date.now() - 5 * 60_000).toISOString(),
          }),
        ],
      })
    )

    expect(sheet().getByText('Complete')).toBeInTheDocument()
    expect(sheet().getByText('Previous fire').nextSibling).toHaveTextContent('5 min. ago')
    expect(sheet().getByText('Next fire').nextSibling).toHaveTextContent('Not scheduled')
  })

  it("describes a cron trigger's schedule in words", () => {
    renderSheet(job({ triggers: [trigger({ cronExpression: '0 0 9 ? * MON-FRI' })] }))
    expect(sheet().getByText('At 09:00, Monday through Friday')).toBeInTheDocument()
  })

  it('names the close button for screen readers', () => {
    renderSheet(job())
    expect(sheet().getByRole('button', { name: 'Close' })).toBeInTheDocument()
  })

  it('says how many triggers a delete removes', async () => {
    renderSheet(job({ triggers: [trigger()] }))
    await userEvent.click(sheet().getByRole('button', { name: /Delete/ }))
    expect(
      within(screen.getByRole('alertdialog')).getByText(
        /DEFAULT\.nightly and its trigger are removed/
      )
    ).toBeInTheDocument()
  })

  it('does not save while a cron expression is shown as invalid', async () => {
    const { actions } = renderSheet(job())
    vi.mocked(api).mockImplementation(async path => ({
      isValid: false,
      expression: new URLSearchParams((path as string).split('?')[1]).get('expression'),
      description: null,
      nextFireTimes: [],
      errorMessage: 'Unexpected end of expression',
    }))
    await userEvent.click(sheet().getByRole('button', { name: /Edit/ }))
    await userEvent.clear(sheet().getByLabelText('Cron expression'))
    await userEvent.type(sheet().getByLabelText('Cron expression'), '0/30 * * * *')

    expect(await sheet().findByText('Unexpected end of expression')).toBeInTheDocument()
    await waitFor(() =>
      expect(sheet().getByRole('button', { name: 'Save changes' })).toBeDisabled()
    )
    expect(actions.update).not.toHaveBeenCalled()
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

  it('stays open when the job could not be deleted', async () => {
    const { actions, onClose } = renderSheet(job())
    vi.mocked(actions.remove).mockResolvedValue(false)

    await userEvent.click(sheet().getByRole('button', { name: /Delete/ }))
    await userEvent.click(screen.getByRole('button', { name: 'Delete job' }))

    expect(actions.remove).toHaveBeenCalledOnce()
    expect(onClose).not.toHaveBeenCalled()
    expect(screen.getByRole('dialog', { name: 'nightly' })).toBeInTheDocument()
  })

  it('leaves edit mode when closed, so the next job opens on its details', async () => {
    const { onClose } = renderSheet(job())
    await userEvent.click(sheet().getByRole('button', { name: /Edit/ }))

    await userEvent.keyboard('{Escape}')

    expect(onClose).toHaveBeenCalled()
    expect(screen.queryByRole('dialog')).toBeNull()
  })

  it('asks before Escape closes unsaved edits, and keeps them when told to', async () => {
    const { onClose } = renderSheet(job({ description: 'old' }))
    await userEvent.click(sheet().getByRole('button', { name: /Edit/ }))
    await userEvent.type(sheet().getByLabelText('Description'), ' and new')

    await userEvent.keyboard('{Escape}')
    const confirm = within(screen.getByRole('alertdialog', { name: 'Discard unsaved changes?' }))
    await userEvent.click(confirm.getByRole('button', { name: 'Keep editing' }))

    expect(onClose).not.toHaveBeenCalled()
    expect(sheet().getByLabelText('Description')).toHaveValue('old and new')
  })

  it('closes without saving once told to discard the edits', async () => {
    const { actions, onClose } = renderSheet(job({ description: 'old' }))
    await userEvent.click(sheet().getByRole('button', { name: /Edit/ }))
    await userEvent.type(sheet().getByLabelText('Description'), ' and new')

    await userEvent.click(sheet().getByRole('button', { name: 'Close' }))
    await userEvent.click(screen.getByRole('button', { name: 'Discard' }))

    expect(onClose).toHaveBeenCalled()
    expect(actions.update).not.toHaveBeenCalled()
    expect(screen.queryByRole('dialog')).toBeNull()
  })

  it('asks before Escape closes a run whose data was changed', async () => {
    const { onClose } = renderSheet(job({ data: { region: 'eu' } }))
    await userEvent.click(sheet().getByRole('button', { name: /Run with different data/ }))
    await userEvent.clear(sheet().getByLabelText('Value for region'))
    await userEvent.type(sheet().getByLabelText('Value for region'), 'us')

    await userEvent.keyboard('{Escape}')

    expect(
      screen.getByRole('alertdialog', { name: 'Discard unsaved changes?' })
    ).toBeInTheDocument()
    expect(onClose).not.toHaveBeenCalled()
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
    const runs = within(sheet().getByText('Recent runs').closest('section')!)
    expect(runs.getByText('Failed')).toBeInTheDocument()
    expect(runs.getByText('Succeeded')).toBeInTheDocument()
    // More than are listed, for the chart of their durations
    const path = vi
      .mocked(api)
      .mock.calls.map(c => c[0] as string)
      .find(p => p.startsWith('history?'))!
    expect(Object.fromEntries(new URLSearchParams(path.split('?')[1]))).toMatchObject({
      limit: '30',
      group: 'billing',
      name: 'nightly',
    })

    await userEvent.click(sheet().getByRole('button', { name: /All runs/ }))
    expect(onViewHistory).toHaveBeenCalledWith('billing', 'nightly')
  })

  it("shows the job's figures for a day and a week, and its recent runs' durations", async () => {
    history = {
      records: [
        execution(3, { status: 'Failed', durationMs: 4000 }),
        execution(2, { durationMs: 1000 }),
        execution(1, { status: 'Misfired', durationMs: null }),
      ],
      nextBefore: null,
    }
    vi.mocked(api).mockImplementation(async path => {
      const params = new URLSearchParams((path as string).split('?')[1])
      if ((path as string).startsWith('history/stats'))
        return params.get('hours') === '24'
          ? historyStats({ runs: 10, successRate: 1, averageDurationMs: 800 })
          : historyStats({ runs: 70, successRate: 0.8, averageDurationMs: 2500 })
      return history
    })
    renderSheet(job({ group: 'billing' }), { historyEnabled: true })

    const table = within(await sheet().findByRole('table'))
    const row = (label: string) =>
      Array.from(
        table.getByRole('rowheader', { name: label }).parentElement!.querySelectorAll('td'),
        td => td.textContent
      )
    expect(await table.findByText('70')).toBeInTheDocument()
    expect(row('Runs')).toEqual(['10', '70'])
    expect(row('Success rate')).toEqual(['100%', '80%'])
    expect(row('Average duration')).toEqual(['800ms', '2.5s'])

    // The runs that ran, oldest first: a misfire has no duration
    const chart = sheet().getByRole('img', { name: /Durations of the last 2 runs/ })
    expect(chart).toHaveAccessibleName(
      'Durations of the last 2 runs, oldest first: 1.0s succeeded, 4.0s failed'
    )
    expect(sheet().getByText('longest 4.0s')).toBeInTheDocument()
  })

  it('marks misfires, retry attempts and late starts among the recent runs', async () => {
    const minutesAgo = (m: number) => new Date(Date.now() - m * 60_000).toISOString()
    history = {
      records: [
        execution(3, {
          status: 'Misfired',
          scheduledFireTimeUtc: minutesAgo(10),
          firedTimeUtc: minutesAgo(0),
          error: 'Missed its fire time; Quartz skips it',
        }),
        execution(2, { status: 'Failed', attempt: 2 }),
        execution(1, { scheduledFireTimeUtc: minutesAgo(31), firedTimeUtc: minutesAgo(30) }),
      ],
      nextBefore: null,
    }
    renderSheet(job(), { historyEnabled: true })

    expect(await sheet().findByText('missed 10 min. ago')).toBeInTheDocument()
    // What Quartz did about a misfire is not a failure
    expect(sheet().getByText('Missed its fire time; Quartz skips it')).not.toHaveClass(
      'text-destructive'
    )
    expect(sheet().getByText('attempt 2')).toBeInTheDocument()
    expect(sheet().getByText('started 1m late')).toBeInTheDocument()
  })

  it('says why the recent runs could not be loaded', async () => {
    vi.mocked(api).mockRejectedValue(new Error('History table is missing'))
    renderSheet(job(), { historyEnabled: true })

    // The run statistics say so too, rather than load for ever
    expect(await sheet().findAllByText('History table is missing')).toHaveLength(2)
    expect(sheet().queryByText('Loading…')).toBeNull()
    expect(sheet().queryByRole('table')).toBeNull()
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
