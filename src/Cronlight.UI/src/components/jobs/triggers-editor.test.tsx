import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useState } from 'react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { api } from '@/api'
import { TriggersEditor } from '@/components/jobs/triggers-editor'
import { formatDateTime } from '@/lib/format'
import {
  draftsFromTriggers,
  draftsToRequest,
  emptyTrigger,
  type TriggerDraft,
} from '@/lib/job-form'
import { trigger } from '@/test/fixtures'

// The cron preview asks the server; answer every expression as valid
vi.mock('@/api', () => ({ api: vi.fn() }))

/** Renders the editor with real state and shows the triggers the form would send, as the job forms do. */
function Editor({
  initial,
  onInvalidChange,
}: {
  initial: TriggerDraft[]
  onInvalidChange?: (invalid: boolean) => void
}) {
  const [triggers, setTriggers] = useState(initial)
  return (
    <>
      <TriggersEditor
        triggers={triggers}
        onChange={setTriggers}
        onInvalidChange={onInvalidChange}
      />
      <output data-testid="request">{JSON.stringify(draftsToRequest(triggers))}</output>
    </>
  )
}
const sent = () => JSON.parse(screen.getByTestId('request').textContent!) as unknown[]

// A datetime-local input takes a whole value; typed character by character it is not a time until the end
const setTime = (label: string, value: string) =>
  fireEvent.change(screen.getByLabelText(label), { target: { value } })

async function chooseKind(label: string) {
  await userEvent.click(screen.getByRole('combobox', { name: 'Trigger kind' }))
  await userEvent.click(screen.getByRole('option', { name: label }))
}

const syntaxLinks = () => screen.queryAllByRole('link', { name: /Quartz cron syntax/ })

describe('TriggersEditor', () => {
  beforeEach(() => {
    vi.mocked(api).mockImplementation(async path => {
      const expression = new URLSearchParams((path as string).split('?')[1]).get('expression')
      return {
        isValid: true,
        expression,
        description: null,
        nextFireTimes: ['2030-01-01T09:00:00Z'],
        errorMessage: null,
      }
    })
  })

  it('labels each field on screen, so a filled one still says what it is', async () => {
    render(<Editor initial={[emptyTrigger()]} />)
    const labels = () => screen.getAllByText(/./, { selector: 'label' }).map(l => l.textContent)

    expect(labels()).toEqual(
      expect.arrayContaining(['Trigger kind', 'Cron expression', 'Time zone', 'Trigger name'])
    )
    await chooseKind('Interval')
    expect(labels()).toEqual(
      expect.arrayContaining(['Interval', 'Interval unit', 'First run', 'Number of runs'])
    )
    await chooseKind('One time')
    expect(labels()).toEqual(expect.arrayContaining(['Run at']))
  })

  it('marks an invalid cron expression on its field, with the reason as its description', async () => {
    vi.mocked(api).mockImplementation(async path => ({
      isValid: false,
      expression: new URLSearchParams((path as string).split('?')[1]).get('expression'),
      description: null,
      nextFireTimes: [],
      errorMessage: 'Hour values must be between 0 and 23',
    }))
    render(<Editor initial={[emptyTrigger()]} />)
    const field = screen.getByLabelText('Cron expression')

    await userEvent.type(field, '0 0 25 * * ?')

    await waitFor(() => expect(field).toHaveAttribute('aria-invalid', 'true'))
    expect(field).toHaveAccessibleDescription('Hour values must be between 0 and 23')
  })

  it('fills the cron field from a common schedule', async () => {
    render(<Editor initial={[emptyTrigger()]} />)

    await userEvent.click(screen.getByRole('button', { name: 'Common schedules' }))
    await userEvent.click(screen.getByRole('menuitem', { name: /Weekdays at 09:00/ }))

    expect(screen.getByLabelText('Cron expression')).toHaveValue('0 0 9 ? * MON-FRI')
    expect(sent()).toEqual([expect.objectContaining({ cronExpression: '0 0 9 ? * MON-FRI' })])
  })

  it("suggests time zones as one is typed, the browser's own first", () => {
    render(<Editor initial={[emptyTrigger()]} />)

    const list = document.getElementById(screen.getByLabelText('Time zone').getAttribute('list')!)
    const zones = [...list!.querySelectorAll('option')].map(o => o.value)
    expect(zones[0]).toBe(Intl.DateTimeFormat().resolvedOptions().timeZone)
    expect(zones).toContain('Europe/Paris')
    expect(new Set(zones).size).toBe(zones.length)
  })

  it("links to Quartz.NET's cron documentation, in a new tab", () => {
    render(<Editor initial={[emptyTrigger()]} />)

    for (const link of syntaxLinks()) {
      expect(link).toHaveAttribute(
        'href',
        'https://www.quartz-scheduler.net/documentation/quartz-3.x/tutorial/crontriggers.html'
      )
      expect(link).toHaveAttribute('target', '_blank')
      expect(link).toHaveAttribute('rel', 'noopener noreferrer')
    }
  })

  it('offers the cron syntax under a cron box while it is empty or invalid', async () => {
    vi.mocked(api).mockImplementation(async path => {
      const expression = new URLSearchParams((path as string).split('?')[1]).get('expression')
      const isValid = expression === '0 0 9 * * ?'
      return {
        isValid,
        expression,
        description: null,
        nextFireTimes: isValid ? ['2030-01-01T09:00:00Z'] : [],
        errorMessage: isValid ? null : 'Unexpected end of expression.',
      }
    })
    render(<Editor initial={[emptyTrigger()]} />)
    const cron = screen.getByLabelText('Cron expression')
    // Under the box while it is empty
    expect(syntaxLinks()).toHaveLength(1)

    await userEvent.type(cron, '0 0 9 * * ?')
    // The preview waits for typing to pause before it asks the server
    await screen.findByText('At 09:00', {}, { timeout: 3000 })
    expect(syntaxLinks()).toHaveLength(0)

    await userEvent.clear(cron)
    await userEvent.type(cron, '0 0/30 * * *')
    // The preview reports the expression invalid to the editor in the render after it shows the error
    await waitFor(() => expect(syntaxLinks()).toHaveLength(1), { timeout: 3000 })
    expect(screen.getByText('Unexpected end of expression.')).toBeInTheDocument()
  })

  it('adds and removes trigger rows', async () => {
    render(<Editor initial={[emptyTrigger()]} />)

    await userEvent.click(screen.getByRole('button', { name: /Add trigger/ }))
    expect(screen.getAllByLabelText('Cron expression')).toHaveLength(2)

    await userEvent.click(screen.getAllByRole('button', { name: 'Remove trigger' })[0])
    expect(screen.getAllByLabelText('Cron expression')).toHaveLength(1)
  })

  it('keeps each row’s own state when an earlier row is removed', async () => {
    render(<Editor initial={[emptyTrigger(), emptyTrigger()]} />)
    await userEvent.type(screen.getAllByLabelText('Cron expression')[0], '0 0 9 * * ?')
    await userEvent.type(screen.getAllByLabelText('Cron expression')[1], '0 0 5 * * ?')
    expect(await screen.findByText('At 05:00')).toBeInTheDocument()

    await userEvent.click(screen.getAllByRole('button', { name: 'Remove trigger' })[0])

    expect(screen.getByLabelText('Cron expression')).toHaveValue('0 0 5 * * ?')
    expect(screen.queryByText('At 09:00')).toBeNull()
    expect(screen.getByText('At 05:00')).toBeInTheDocument()
  })

  it('previews a cron expression once it is long enough, describing it in words', async () => {
    render(<Editor initial={[emptyTrigger()]} />)

    await userEvent.type(screen.getByLabelText('Cron expression'), '0 0 9 * * ?')

    expect(await screen.findByText('At 09:00')).toBeInTheDocument()
    expect(vi.mocked(api)).toHaveBeenCalledWith(
      expect.stringContaining('cron/preview?expression=0+0+9'),
      expect.anything()
    )
  })

  it('says an interval trigger needs two runs or more, pointing to one-time triggers', async () => {
    render(<Editor initial={[{ ...emptyTrigger('Interval'), every: '5', runs: '1' }]} />)

    expect(screen.getByText(/For a single run, choose One time/)).toBeInTheDocument()
    await userEvent.clear(screen.getByLabelText('Number of runs'))
    await userEvent.type(screen.getByLabelText('Number of runs'), '3')
    expect(screen.getByText(/Every 5m, starting now, 3 runs in all/)).toBeInTheDocument()
  })

  it('says a trigger keeps the calendar set on it, which the form does not send', () => {
    render(
      <Editor
        initial={draftsFromTriggers([
          trigger({
            name: 'payroll',
            cronExpression: '0 0 9 ? * MON-FRI',
            calendarName: 'holidays',
          }),
          trigger({ name: 'audit', cronExpression: '0 0 18 * * ?' }),
        ])}
      />
    )

    expect(screen.getAllByText(/Skips the times excluded by its calendar/)).toHaveLength(1)
    expect(screen.getByText('holidays')).toBeInTheDocument()
    expect(sent()).toEqual([
      expect.not.objectContaining({ calendarName: expect.anything() }),
      expect.not.objectContaining({ calendarName: expect.anything() }),
    ])
  })

  it('sends the name, time zone and skip choice of a cron trigger', async () => {
    render(<Editor initial={[emptyTrigger()]} />)

    await userEvent.type(screen.getByLabelText('Cron expression'), '0 0 9 * * ?')
    await userEvent.type(screen.getByLabelText('Time zone'), 'Europe/Paris')
    await userEvent.type(screen.getByLabelText('Trigger name'), 'mornings')
    await userEvent.click(screen.getByRole('checkbox', { name: /Skip missed runs/ }))

    expect(sent()).toEqual([
      {
        name: 'mornings',
        skipMissed: true,
        cronExpression: '0 0 9 * * ?',
        timeZone: 'Europe/Paris',
      },
    ])
  })

  it('asks when a one-time trigger runs, then says when, in local time', async () => {
    render(<Editor initial={[emptyTrigger()]} />)

    await chooseKind('One time')
    expect(screen.queryByLabelText('Cron expression')).toBeNull()
    expect(screen.getByText('Choose when it runs.')).toBeInTheDocument()
    expect(screen.getByText(/it then never runs/)).toBeInTheDocument()
    // Not sent until it says when
    expect(sent()).toEqual([])

    setTime('Run at', '2030-01-01T09:00')

    const at = new Date('2030-01-01T09:00').toISOString()
    expect(screen.getByText(`Runs once, ${formatDateTime(at)}`)).toBeInTheDocument()
    expect(sent()).toEqual([{ name: null, kind: 'Once', startAtUtc: at }])
  })

  it('builds an interval trigger from how often, its unit, its first run and how many runs', async () => {
    render(<Editor initial={[emptyTrigger()]} />)

    await chooseKind('Interval')
    expect(screen.getByText('Enter how often it runs.')).toBeInTheDocument()
    await userEvent.type(screen.getByLabelText('Interval'), '2')
    await userEvent.click(screen.getByRole('combobox', { name: 'Interval unit' }))
    await userEvent.click(screen.getByRole('option', { name: 'hours' }))
    expect(screen.getByText('Every 2h, starting now, forever')).toBeInTheDocument()

    setTime('First run', '2030-01-01T09:00')
    await userEvent.type(screen.getByLabelText('Number of runs'), '4')

    const at = new Date('2030-01-01T09:00').toISOString()
    expect(
      screen.getByText(`Every 2h, starting ${formatDateTime(at)}, 4 runs in all`)
    ).toBeInTheDocument()
    expect(sent()).toEqual([
      { name: null, kind: 'Interval', intervalSeconds: 7200, startAtUtc: at, repeatCount: 3 },
    ])
  })

  it('treats a cron expression the server could not check as invalid, saying why', async () => {
    vi.mocked(api).mockRejectedValue(new Error('Service Unavailable'))
    const onInvalidChange = vi.fn()
    render(<Editor initial={[emptyTrigger()]} onInvalidChange={onInvalidChange} />)

    await userEvent.type(screen.getByLabelText('Cron expression'), '0 0 9 * * ?')

    expect(
      await screen.findByText('Service Unavailable', {}, { timeout: 3000 })
    ).toBeInTheDocument()
    await waitFor(() => expect(onInvalidChange).toHaveBeenLastCalledWith(true))
  })

  it('no longer holds the form back once an invalid cron row becomes another kind', async () => {
    vi.mocked(api).mockImplementation(async path => ({
      isValid: false,
      expression: new URLSearchParams((path as string).split('?')[1]).get('expression'),
      description: null,
      nextFireTimes: [],
      errorMessage: 'Unexpected end of expression.',
    }))
    const onInvalidChange = vi.fn()
    render(<Editor initial={[emptyTrigger()]} onInvalidChange={onInvalidChange} />)
    await userEvent.type(screen.getByLabelText('Cron expression'), '0/30 * * * *')
    await waitFor(() => expect(onInvalidChange).toHaveBeenLastCalledWith(true), { timeout: 3000 })

    await chooseKind('One time')

    expect(onInvalidChange).toHaveBeenLastCalledWith(false)
  })

  it('lists every next fire time the server previews, and names a schedule it cannot describe', async () => {
    vi.mocked(api).mockImplementation(async path => ({
      isValid: true,
      expression: new URLSearchParams((path as string).split('?')[1]).get('expression'),
      description: null,
      nextFireTimes: ['2030-01-01T09:00:00Z', '2030-01-02T09:00:00Z'],
      errorMessage: null,
    }))
    render(<Editor initial={[emptyTrigger()]} />)

    // The server finds it valid, but the library that puts schedules in words cannot read it
    await userEvent.type(screen.getByLabelText('Cron expression'), '0 0 9 L-3W * ?')

    expect(
      await screen.findByText('Valid cron expression', {}, { timeout: 3000 })
    ).toBeInTheDocument()
    expect(
      within(screen.getByRole('list', { name: 'Next (your local time)' }))
        .getAllByRole('listitem')
        .map(li => li.textContent)
    ).toEqual([formatDateTime('2030-01-01T09:00:00Z'), formatDateTime('2030-01-02T09:00:00Z')])
  })
})
